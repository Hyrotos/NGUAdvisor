using System;
using System.Diagnostics;
using System.IO;

namespace NGUAdvisorLauncher
{
    // Mirrors "Run NGU Advisor.bat" (hot-reload flow) so the double-clicked launcher carries the advisor
    // icon. Runs from its own folder: hands the bootstrap the injector path, then injects the bootstrap
    // via smi.exe, which byte-loads NGUAdvisor.dll. On failure it pauses so the error stays readable.
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string injector = Path.Combine(dir, "injector");
                string smi = Path.Combine(injector, "smi.exe");

                if (!File.Exists(smi))
                {
                    Console.Error.WriteLine("Could not find injector\\smi.exe next to this launcher.");
                    Console.Error.WriteLine("Run it from the extracted NGU Advisor folder.");
                    return Fail();
                }

                // The bootstrap is byte-loaded and can't know where it lives — write it the injector path.
                string low = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "NGUAdvisor");
                Directory.CreateDirectory(low);
                File.WriteAllText(Path.Combine(low, "injector-path.txt"), injector);

                var psi = new ProcessStartInfo(smi,
                    "inject -p NGUIdle -a .\\injector\\NGUAdvisor.dll -n NGUAdvisor -c Loader -m Init")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false
                };

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Injection failed — is NGU Idle running?");
                        return Fail(p.ExitCode);
                    }
                    return 0;   // success: close promptly
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Launcher error: " + e.Message);
                return Fail();
            }
        }

        private static int Fail(int code = 1)
        {
            try { Console.Error.WriteLine("Press any key to close..."); Console.ReadKey(true); } catch { }
            return code;
        }
    }
}
