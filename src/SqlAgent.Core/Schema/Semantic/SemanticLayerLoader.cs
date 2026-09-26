namespace SqlAgent.Core.Schema;

/// <summary>
/// Merges the parsed semantic.yaml with the introspected schema into a <see cref="SchemaCatalog"/>: checks that
/// every referenced table and column exists (fail fast, with the YAML path), expands deny globs against the real
/// columns, merges the global deny list into each role, and checks the examples against each role's allowlist.
/// </summary>
public static class SemanticLayerLoader
{
    /// <summary>Startup path: parse semantic.yaml, introspect as the app user, and build the catalog.</summary>
    public static async Task<SchemaCatalog> LoadAsync(
        AgentAssetsOptions assets,
        string appConnectionString,
        CancellationToken ct = default)
    {
        var config = SemanticConfigParser.Load(assets);
        var schema = await new SchemaIntrospector().IntrospectAsync(appConnectionString, ct);
        return Build(config, schema);
    }

    public static SchemaCatalog Build(SemanticConfig config, DatabaseSchema schema)
    {
        var errors = new List<string>();

        // Each deny rule is expanded once, then restricted to the tables of the role it is merged into.
        var globalExpansion = config.GlobalDenyColumns
            .Select(rule => Expand(rule, schema, "global_deny_columns", errors)).ToList();

        var tablesByRole = new Dictionary<Role, List<TableInfo>>();
        var deniedByRole = new Dictionary<Role, Dictionary<string, HashSet<string>>>();
        foreach (var (role, roleConfig) in config.Roles)
        {
            var path = $"roles.{role.ToKey()}";
            var tables = new List<TableInfo>();
            foreach (var name in roleConfig.Tables)
            {
                if (schema.TryGetTable(name, out var table)) tables.Add(table);
                else errors.Add($"{path}.tables: table {name} does not exist in the database");
            }

            tablesByRole[role] = tables.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();

            var roleExpansion = roleConfig.DenyColumns
                .Select(rule => Expand(rule, schema, $"{path}.deny_columns", errors));
            var denied = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var readable = tables.Select(t => t.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, columns) in globalExpansion.Concat(roleExpansion).SelectMany(e => e))
            {
                if (columns.Count == 0 || !readable.Contains(table)) continue;
                if (!denied.TryGetValue(table, out var set)) denied[table] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.UnionWith(columns);
            }

            deniedByRole[role] = denied;
        }

        CheckTableMetadata(config, schema, errors);

        var allowLists = tablesByRole.ToDictionary(
            kv => kv.Key,
            kv => new AllowList(
                Frozen.Set(kv.Value.Select(t => t.FullName)),
                Frozen.Map(deniedByRole[kv.Key].Select(
                    d => KeyValuePair.Create(d.Key, Frozen.Set(d.Value))))));

        for (var i = 0; i < config.Examples.Count; i++)
        {
            foreach (var role in config.Examples[i].Roles.Where(allowLists.ContainsKey))
            {
                var list = allowLists[role];
                foreach (var problem in ExampleSqlValidator.Validate(config.Examples[i].Sql, list.Tables, list.DeniedColumns))
                    errors.Add($"examples[{i}] (role {role.ToKey()}): {problem}");
            }
        }

        if (errors.Count > 0) throw new SemanticLayerException(config.SourcePath, errors);

        var views = new Dictionary<Role, SchemaCatalog.RoleView>();
        foreach (var (role, tables) in tablesByRole)
        {
            var allow = allowLists[role];
            var descriptions = tables.Select(t => Describe(t, config, allow)).ToList();
            views[role] = new SchemaCatalog.RoleView(
                Frozen.List(descriptions.Select(d => new TableSummary(d.Name, d.Description))),
                Frozen.Map(descriptions.Select(d => KeyValuePair.Create(d.Name, d))),
                allow,
                Frozen.List(config.Examples.Where(e => e.Roles.Contains(role))));
        }

        return new SchemaCatalog(views);
    }

    /// <summary>Tables and columns a deny rule matches in the real schema; an entry that matches nothing is an error.</summary>
    private static List<(string Table, HashSet<string> Columns)> Expand(
        DenyRule rule, DatabaseSchema schema, string path, List<string> errors)
    {
        var schemaGlob = new GlobPattern(rule.SchemaPattern);
        var tableGlob = new GlobPattern(rule.TablePattern);
        var matched = schema.Tables.Where(t => schemaGlob.IsMatch(t.Schema) && tableGlob.IsMatch(t.Name)).ToList();
        if (matched.Count == 0)
        {
            errors.Add($"{path}.{rule.TableKey}: no table matches");
            return [];
        }

        var result = matched.Select(t => (t.FullName, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var pattern in rule.ColumnPatterns)
        {
            var columnGlob = new GlobPattern(pattern);
            var any = false;
            for (var i = 0; i < matched.Count; i++)
            {
                foreach (var column in matched[i].Columns.Where(c => columnGlob.IsMatch(c.Name)))
                {
                    result[i].Item2.Add(column.Name);
                    any = true;
                }
            }

            if (!any) errors.Add($"{path}.{rule.TableKey}: no column matches '{pattern}'");
        }

        return result;
    }

    private static void CheckTableMetadata(SemanticConfig config, DatabaseSchema schema, List<string> errors)
    {
        foreach (var (name, meta) in config.Tables)
        {
            if (!schema.TryGetTable(name, out var table))
            {
                errors.Add($"tables.{name}: table does not exist in the database");
                continue;
            }

            foreach (var (term, column) in meta.Synonyms)
            {
                if (!table.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"tables.{name}.synonyms.{term}: column {column} does not exist");
            }
        }

        for (var i = 0; i < config.JoinHints.Count; i++)
        {
            foreach (var name in config.JoinHints[i].Tables.Where(n => !schema.TryGetTable(n, out _)))
                errors.Add($"join_hints[{i}].tables: table {name} does not exist in the database");
        }
    }

    /// <summary>What the model may learn about one table: denied columns, and anything tied to them, are left out.</summary>
    private static TableDescription Describe(TableInfo table, SemanticConfig config, AllowList allow)
    {
        var denied = allow.DeniedColumns.TryGetValue(table.FullName, out var d) ? d : new HashSet<string>();
        config.Tables.TryGetValue(table.FullName, out var meta);

        var columns = table.Columns
            .Where(c => !denied.Contains(c.Name))
            .Select(c => new ColumnDescription(c.Name, c.DataType, c.IsNullable, c.Description));

        // A foreign key is shown only if both ends are readable by this role.
        var foreignKeys = table.ForeignKeys
            .Where(fk => allow.Tables.Contains(fk.ReferencedTable)
                         && !fk.Columns.Any(denied.Contains)
                         && !(allow.DeniedColumns.TryGetValue(fk.ReferencedTable, out var referencedDenied)
                              && fk.ReferencedColumns.Any(referencedDenied.Contains)))
            .Select(fk => new ForeignKeyDescription(Frozen.List(fk.Columns), fk.ReferencedTable, Frozen.List(fk.ReferencedColumns)));

        var synonyms = (meta?.Synonyms ?? new Dictionary<string, string>())
            .Where(s => !denied.Contains(s.Value));

        var hints = config.JoinHints
            .Where(h => h.Tables.Any(t => t.Equals(table.FullName, StringComparison.OrdinalIgnoreCase))
                        && h.Tables.All(allow.Tables.Contains))
            .Select(h => h.Text);

        return new TableDescription(
            table.FullName,
            meta?.Description ?? table.Description,
            Frozen.List(columns),
            Frozen.List(foreignKeys),
            Frozen.Map(synonyms),
            Frozen.List(hints));
    }
}
