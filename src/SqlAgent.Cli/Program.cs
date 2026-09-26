using SqlAgent.Cli.Commands;

// Commands are added with the features they serve: `emit-grants` with the schema catalog,
// `eval` with the evaluation suite.
const string usage = """
    Usage: SqlAgent.Cli <command> [options]

    Commands:
      emit-grants [--yaml <semantic.yaml>] [--out <file>|-]
          Writes deploy/sql/40-role-grants.generated.sql from semantic.yaml. Needs no database.
          Defaults: the embedded semantic.yaml, and the deploy/sql file of the repository found from the
          current directory. "--out -" prints to standard output.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

switch (args[0])
{
    case "emit-grants":
        return EmitGrantsCommand.Run(args[1..]);
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.{Environment.NewLine}{Environment.NewLine}{usage}");
        return 1;
}
