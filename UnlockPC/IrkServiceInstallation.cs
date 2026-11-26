using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace UnlockPC;

internal static class IrkServiceInstallation
{
    internal const string InstallArgument = "--install-irk-service";
    private const string FileName = "UnlockPCIrk.exe";
    internal const string MonitorServiceName = "UnlockPCMonitor";

    private static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnlockPC IRK Service");

    internal static string MonitorLogDirectory => Path.Combine(InstallDirectory, "Logs");

    internal static int Install(string sid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 5;

        try
        {
            var user = new SecurityIdentifier(sid);
            if (!user.IsAccountSid())
                return 87;

            string source = Path.Combine(AppContext.BaseDirectory, FileName);
            string directory = InstallDirectory;
            string target = Path.Combine(directory, FileName);
            string image = $"\"{target}\" {user.Value}";
            string monitorTarget = Path.Combine(directory, "UnlockPCMonitor.exe");
            string workerDirectory = Path.Combine(directory, "Worker");
            string worker = Path.Combine(workerDirectory, "UnlockPC.exe");
            string monitorImage = $"\"{monitorTarget}\" {user.Value} \"{worker}\"";
            using var monitor = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{MonitorServiceName}");
            if (monitor is not null)
            {
                if (monitor.GetValue("ImagePath") is not string existingImage ||
                    !existingImage.StartsWith($"\"{monitorTarget}\" ", StringComparison.OrdinalIgnoreCase))
                    return 87;
                int stopped = RunSc("stop", MonitorServiceName);
                if (stopped is not (0 or 1061 or 1062))
                    return stopped;
            }
            using var existing = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{BondedIrk.ServiceName}");
            if (existing is not null && (existing.GetValue("ImagePath") is not string registered ||
                !registered.StartsWith($"\"{target}\" ", StringComparison.OrdinalIgnoreCase)))
                return 87;

            if (existing is not null)
            {
                int stopped = RunSc("stop", BondedIrk.ServiceName);
                if (stopped is not (0 or 1061 or 1062))
                    return stopped;
            }

            var security = new DirectorySecurity();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddAccess(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddAccess(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddAccess(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
            var destination = new DirectoryInfo(directory);
            if (destination.Exists && (destination.Attributes & FileAttributes.ReparsePoint) != 0)
                return 5;
            if (destination.Exists)
                destination.SetAccessControl(security);
            else
                destination.Create(security);

            InstallBinary(target);
            InstallBinary(monitorTarget);
            var workerDestination = new DirectoryInfo(workerDirectory);
            if (workerDestination.Exists && (workerDestination.Attributes & FileAttributes.ReparsePoint) != 0)
                return 5;
            workerDestination.Create();
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                string targetFile = Path.Combine(workerDirectory, Path.GetFileName(file));
                if (File.Exists(targetFile) && (File.GetAttributes(targetFile) & FileAttributes.ReparsePoint) != 0)
                    return 5;
                File.Copy(file, targetFile, overwrite: true);
            }

            int result = RunSc(existing is null ? "create" : "config", BondedIrk.ServiceName,
                "binPath=", image, "start=", "demand", "obj=", "LocalSystem", "DisplayName=", "UnlockPC IRK Service");
            if (result != 0)
                return result;

            result = RunSc("sdset", BondedIrk.ServiceName, $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LCRPWP;;;{user.Value})");
            if (result == 0)
            {
                using var configured = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{BondedIrk.ServiceName}", writable: true);
                configured?.SetValue("UnlockPCAuthorizedUser", user.Value);
            }
            if (result != 0)
                return result;

            result = RunSc(monitor is null ? "create" : "config", MonitorServiceName,
                "binPath=", monitorImage, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "UnlockPC Watch Monitor");
            if (result != 0)
                return result;
            // SCM restarts the monitor after a failure. SERVICE_STOPPED with exit code zero stays stopped.
            result = RunSc("failure", MonitorServiceName,
                "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000");
            if (result != 0)
                return result;
            result = RunSc("failureflag", MonitorServiceName, "1");
            if (result != 0)
                return result;
            result = RunSc("start", MonitorServiceName);
            return result == 1056 ? 0 : result;

            void InstallBinary(string destinationPath)
            {
                string staged = Path.Combine(directory, $"UnlockPCIrk-{Guid.NewGuid():N}.exe");
                try
                {
                    File.Copy(source, staged);
                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetOwner(administrators);
                    fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        FileSystemRights.FullControl, AccessControlType.Allow));
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                        FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                    new FileInfo(staged).SetAccessControl(fileSecurity);
                    for (int i = 0; ; i++)
                    {
                        try
                        {
                            File.Move(staged, destinationPath, overwrite: true);
                            break;
                        }
                        catch (IOException) when (i < 20) { Thread.Sleep(250); }
                    }
                }
                finally
                {
                    File.Delete(staged);
                }
            }

            void AddAccess(WellKnownSidType account, FileSystemRights rights) =>
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    private static int RunSc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { CreateNoWindow = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null)
            return 1;
        process.WaitForExit();
        return process.ExitCode;
    }
}
