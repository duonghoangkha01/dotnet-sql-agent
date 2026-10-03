using System.Text;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Agent;

/// <summary>Renders <c>describe_tables</c> replies as compact text: a model reads this faster, and cheaper, than JSON.</summary>
public static class SchemaTextRenderer
{
    public static string Render(DescribeTablesResult result)
    {
        var text = new StringBuilder();
        foreach (var table in result.Tables)
        {
            text.Append(table.Name);
            if (table.Description is not null) text.Append(" - ").Append(table.Description);
            text.Append('\n');

            foreach (var column in table.Columns)
            {
                text.Append("  ").Append(column.Name).Append(' ').Append(column.DataType);
                if (column.IsNullable) text.Append(" null");
                if (column.Description is not null) text.Append(" - ").Append(column.Description);
                text.Append('\n');
            }

            foreach (var key in table.ForeignKeys)
            {
                text.Append("  FK (").Append(string.Join(", ", key.Columns)).Append(") -> ")
                    .Append(key.ReferencedTable).Append(" (").Append(string.Join(", ", key.ReferencedColumns)).Append(")\n");
            }

            foreach (var (term, column) in table.Synonyms) text.Append("  \"").Append(term).Append("\" means ").Append(column).Append('\n');
            foreach (var hint in table.JoinHints) text.Append("  Join: ").Append(hint).Append('\n');
            text.Append('\n');
        }

        foreach (var error in result.Errors) text.Append(error).Append('\n');
        return text.ToString().TrimEnd();
    }
}
