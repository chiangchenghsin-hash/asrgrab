using System.Text;

namespace AsrGrab;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // redirected output / no console: ignore
        }

        try
        {
            if (args.Length == 0)
            {
                Out.Error(new CliError(ExitCodes.Usage, "USAGE", "缺少子命令", new()
                {
                    ["hint"] = "asrgrab schema | asrgrab devices --human",
                }));
                return ExitCodes.Usage;
            }

            var cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            Out.Human = rest.Contains("--human", StringComparer.OrdinalIgnoreCase);

            return cmd switch
            {
                "devices" => Commands.Devices(rest),
                "start" => Commands.Start(rest),
                "stop" => Commands.Stop(rest),
                "status" => Commands.Status(rest),
                "transcribe" => Commands.Transcribe(rest),
                "doctor" => Commands.Doctor(rest),
                "schema" => Commands.Schema(rest),
                "models" => Commands.Models(rest),
                "demo" => Commands.Demo(rest),
                "_run" => Commands.RunDaemon(rest),
                "help" or "--help" or "-h" => Commands.Help(),
                _ => throw new CliError(ExitCodes.Usage, "USAGE", $"未知命令: {cmd}", new()
                {
                    ["hint"] = "asrgrab schema",
                }),
            };
        }
        catch (CliError e)
        {
            Out.Error(e);
            return e.ExitCode;
        }
        catch (Exception e)
        {
            Out.Error(new CliError(ExitCodes.Internal, "INTERNAL", e.Message));
            return ExitCodes.Internal;
        }
    }
}
