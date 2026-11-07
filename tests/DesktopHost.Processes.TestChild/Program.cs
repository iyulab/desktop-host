// Arguments, read in order:
//   exit <code>            exit with <code> at once
//   sleep <ms>             wait, then go on to the next argument
//   stderr <text>          write <text> as one standard-error line
//   ready                  write "ready" to standard output
//   echo                   copy standard-input lines to standard output until "stop" arrives, then exit 0
//   ignore-stop            like echo, but a "stop" line is answered "no" and ignored
//   spawn-grandchild       start this program again as `sleep 600000` and print its pid
//   env <name>             print the variable's value, or "<unset>"
//   supervise <outlive>    supervise this program run as `sleep 600000` (ChildOutlivesSupervisor = <outlive>, true|false),
//                          print the supervised child's pid, then wait to be killed
// With no exit argument the program ends with code 0 after the last argument.
using System.Diagnostics;

// UTF-8 on every platform (Windows' default is the console code page), so a test can name what the supervisor decodes.
Console.OutputEncoding = System.Text.Encoding.UTF8;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "exit":
            return int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        case "sleep":
            Thread.Sleep(int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "stderr":
            Console.Error.WriteLine(args[++i]);
            Console.Error.Flush();
            break;
        case "ready":
            Console.WriteLine("ready");
            Console.Out.Flush();
            break;
        case "env":
            Console.WriteLine(Environment.GetEnvironmentVariable(args[++i]) ?? "<unset>");
            Console.Out.Flush();
            break;
        case "supervise":
            var outlive = bool.Parse(args[++i]);
            var me = Environment.ProcessPath!;
            var childArgs = new List<string>();
            if (Path.GetFileNameWithoutExtension(me).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                childArgs.Add(typeof(Program).Assembly.Location);
            childArgs.AddRange(["sleep", "600000"]);
            var supervisor = new DesktopHost.Processes.ProcessSupervisor(new DesktopHost.Processes.ProcessSupervisorOptions
            {
                Name = "outliving-child",
                Command = () => new DesktopHost.Processes.SupervisedCommand(me, childArgs, DesktopHost.Processes.SupervisedCommand.SafeEnvironment()),
                ChildOutlivesSupervisor = outlive,
            });
            supervisor.StartAsync().GetAwaiter().GetResult();
            Console.WriteLine(supervisor.Pid!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        case "spawn-grandchild":
            var self = Environment.ProcessPath!;
            var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(typeof(Program).Assembly.Location);
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add("600000");
            using (var grandchild = Process.Start(psi)!)
            {
                Console.WriteLine(grandchild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Console.Out.Flush();
            }
            break;
        case "echo":
        case "ignore-stop":
            var ignore = args[i] == "ignore-stop";
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                if (line == "stop")
                {
                    if (!ignore) return 0;
                    Console.WriteLine("no");
                }
                else
                {
                    Console.WriteLine(line);
                }
                Console.Out.Flush();
            }
            if (ignore) Thread.Sleep(Timeout.Infinite);
            return 0;
    }
}
return 0;
