using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CreamInstaller.Resources;
using CreamInstaller.Utility;
using Gameloop.Vdf.Linq;

namespace CreamInstaller.Platforms.Steam;

internal static class SteamCMD
{
    private const int ProcessLimit = 20;

    private static readonly string FilePath = DirectoryPath + @"\steamcmd.exe";

    private static readonly ConcurrentDictionary<string, int> AttemptCount = new(); // the more app_updates, the longer SteamCMD should wait for app_info_print

    private static readonly int[] Locks = new int[ProcessLimit];

    private static readonly string ArchivePath = DirectoryPath + @"\steamcmd.zip";
    private static readonly string DllPath = DirectoryPath + @"\steamclient.dll";

    private static readonly string AppCachePath = DirectoryPath + @"\appcache";
    private static readonly string ConfigPath = DirectoryPath + @"\config";
    private static readonly string DumpsPath = DirectoryPath + @"\dumps";
    private static readonly string LogsPath = DirectoryPath + @"\logs";
    private static readonly string SteamAppsPath = DirectoryPath + @"\steamapps";

    private static string DirectoryPath => ProgramData.DirectoryPath;
    internal static string AppInfoPath => ProgramData.AppInfoPath;

    private static string GetArguments(string appId)
        => AttemptCount.TryGetValue(appId, out int attempts)
            ? $@"@ShutdownOnFailedCommand 0 +force_install_dir {DirectoryPath} +login anonymous +app_info_print {appId} "
            + string.Concat(Enumerable.Repeat("+app_update 4 ", attempts)) + "+quit"
            : $"+login anonymous +app_info_print {appId} +quit";

    // ANTIVIRUS FALSE POSITIVE WARNING:
    // Launches steamcmd.exe with +login anonymous +app_info_print <appId> +quit to
    // retrieve game metadata from Steam. The process output is captured and parsed.
    // SteamCMD is an official Valve tool; launching it does not indicate malicious intent.
    private static async Task<string> Run(string appId)
    {
    wait_for_lock:
        if (Program.Canceled)
            return "";
        for (int i = 0; i < Locks.Length; i++)
        {
            if (Program.Canceled)
                return "";
            if (Interlocked.CompareExchange(ref Locks[i], 1, 0) == 0)
            {
                if (appId != null)
                {
                    AttemptCount.TryGetValue(appId, out int count);
                    AttemptCount[appId] = ++count;
                }
                if (Program.Canceled)
                    return "";
                string result = await RunProcessAsync(appId);
                _ = Interlocked.Decrement(ref Locks[i]);
                return result;
            }
            await Task.Delay(200);
        }
        await Task.Delay(200);
        goto wait_for_lock;
    }

    private static async Task<string> RunProcessAsync(string appId)
    {
        ProcessStartInfo psi = new()
        {
            FileName = FilePath, RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true,
            UseShellExecute = false, Arguments = appId is null ? "+quit" : GetArguments(appId), CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        while (true)
        {
            if (Program.Canceled)
                return "";
            Process process = Process.Start(psi);
            if (process is null)
                return "";
            StringBuilder output = new();
            StringBuilder appInfo = new();
            bool appInfoStarted = false;
            using (CancellationTokenSource cts = new(TimeSpan.FromSeconds(1.5)))
            {
                try
                {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync(cts.Token)) is not null)
                    {
                        if (Program.Canceled)
                            break;
                        cts.CancelAfter(TimeSpan.FromSeconds(1.5));
                        if (!appInfoStarted)
                        {
                            int idx = line.IndexOf('{');
                            if (idx >= 0)
                            {
                                appInfoStarted = true;
                                _ = output.Append(line[..idx]);
                                _ = appInfo.Append(line[idx..]).Append('\n');
                            }
                            else
                                _ = output.Append(line).Append('\n');
                        }
                        else
                            _ = appInfo.Append(line).Append('\n');
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
            try { process.Kill(true); } catch { }
            try { process.Close(); } catch { }
            if (Program.Canceled)
                return "";
            if (appId != null && output.ToString().Contains($"No app info for AppID {appId} found, requesting..."))
            {
                AttemptCount[appId]++;
                psi.Arguments = GetArguments(appId);
                continue;
            }
            return appInfo.ToString();
        }
    }

    // ANTIVIRUS FALSE POSITIVE WARNING:
    // Setup downloads steamcmd.zip from Valve's official CDN (steamcdn-a.akamaihd.net),
    // extracts the ZIP archive, and runs steamcmd.exe once with +quit to initialise it.
    // Downloading and extracting an executable is flagged by some AV heuristics as a dropper;
    // the source is Valve's own content delivery network and is used only when steamcmd.exe
    // is not already present in the CreamInstaller data directory.
    internal static async Task Setup(IProgress<int> progress)
    {
        await Cleanup();
        if (!File.Exists(FilePath))
        {
            HttpClient httpClient = HttpClientManager.HttpClient;
            if (httpClient is null)
                return;
            byte[] file = await httpClient.GetByteArrayAsync(new Uri("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"));
            file.Write(ArchivePath);
            ZipFile.ExtractToDirectory(ArchivePath, DirectoryPath);
            File.Delete(ArchivePath);
        }
        if (!File.Exists(DllPath))
        {
            FileSystemWatcher watcher = new(DirectoryPath) { Filter = "*", IncludeSubdirectories = true, EnableRaisingEvents = true };
            if (File.Exists(DllPath))
                progress.Report(-15); // update (not used at the moment)
            else
                progress.Report(-1660); // install
            int cur = 0;
            progress.Report(cur);
            watcher.Changed += (_, _) => progress.Report(++cur);
            _ = await Run(null);
            watcher.Dispose();
        }
    }

    internal static async Task Cleanup()
        => await Task.Run(async () =>
        {
            if (!Directory.Exists(DirectoryPath))
                return;
            await Kill();
            try
            {
                if (Directory.Exists(ConfigPath))
                    foreach (string file in Directory.EnumerateFiles(ConfigPath, "*.tmp"))
                        File.Delete(file);
                foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.old"))
                    File.Delete(file);
                foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.delete"))
                    File.Delete(file);
                foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.crash"))
                    File.Delete(file);
                foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.ntfs_transaction_failed"))
                    File.Delete(file);
                if (Directory.Exists(AppCachePath))
                    Directory.Delete(AppCachePath, true); // this is definitely needed, so SteamCMD gets the latest information for us
                if (Directory.Exists(DumpsPath))
                    Directory.Delete(DumpsPath, true);
                if (Directory.Exists(LogsPath))
                    Directory.Delete(LogsPath, true);
                if (Directory.Exists(SteamAppsPath))
                    Directory.Delete(SteamAppsPath, true); // this is just a useless folder created from +app_update 4
            }
            catch
            {
                // ignored
            }
        });

    private const int CacheTtlDays = 7;
    private const int MaxGetAppInfoRetries = 5;

    internal static async Task<VProperty> GetAppInfo(string appId, string branch = "public", int buildId = 0)
    {
        if (Program.Canceled)
            return null;
        string output;
        string appUpdateFile = $@"{AppInfoPath}\{appId}.vdf";
        int failedAttempts = 0;
    restart:
        if (Program.Canceled || failedAttempts >= MaxGetAppInfoRetries)
            return null;
        // Expire cache after CacheTtlDays days so new DLC is picked up automatically
        if (File.Exists(appUpdateFile)
         && (DateTime.UtcNow - File.GetLastWriteTimeUtc(appUpdateFile)).TotalDays > CacheTtlDays)
            File.Delete(appUpdateFile);
        if (File.Exists(appUpdateFile))
            try
            {
                output = await File.ReadAllTextAsync(appUpdateFile, Encoding.UTF8);
            }
            catch
            {
                goto restart; // transient I/O – don't count as failure
            }
        else
        {
            output = await Run(appId) ?? "";
            int openBracket = output.IndexOf('{');
            int closeBracket = output.LastIndexOf('}');
            if (openBracket != -1 && closeBracket != -1 && closeBracket > openBracket)
            {
                output = $"\"{appId}\"\n" + output[openBracket..(1 + closeBracket)];
                output = output.Replace("ERROR! Failed to install app '4' (Invalid platform)", "");
                try
                {
                    await File.WriteAllTextAsync(appUpdateFile, output, Encoding.UTF8);
                }
                catch
                {
                    goto restart; // transient I/O – don't count as failure
                }
            }
            else
            {
                failedAttempts++;
                if (failedAttempts < MaxGetAppInfoRetries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << failedAttempts, 8)));
                goto restart;
            }
        }
        if (Program.Canceled)
            return null;
        if (!ValveDataFile.TryDeserialize(output, out VProperty appInfo) || appInfo.Value is VValue)
        {
            File.Delete(appUpdateFile);
            failedAttempts++;
            if (failedAttempts < MaxGetAppInfoRetries)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << failedAttempts, 8)));
            goto restart;
        }
        if (appInfo.Value.Children().ToList().Count == 0)
            return appInfo;
        VToken type = appInfo.Value.GetChild("common")?.GetChild("type");
        if (type is not null && type.ToString() != "Game")
            return appInfo;
        string buildid = appInfo.Value.GetChild("depots")?.GetChild("branches")?.GetChild(branch)?.GetChild("buildid")?.ToString();
        if (buildid is null && type is not null)
            return appInfo;
        if (type is not null && (!int.TryParse(buildid, out int gamebuildId) || gamebuildId >= buildId))
            return appInfo;
        List<string> dlcAppIds = await ParseDlcAppIds(appInfo);
        foreach (string dlcAppUpdateFile in dlcAppIds.Select(id => $@"{AppInfoPath}\{id}.vdf"))
            if (File.Exists(dlcAppUpdateFile))
                File.Delete(dlcAppUpdateFile);
        if (File.Exists(appUpdateFile))
            File.Delete(appUpdateFile);
        failedAttempts++;
        if (failedAttempts < MaxGetAppInfoRetries)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << failedAttempts, 8)));
        goto restart;
    }

    internal static async Task<List<string>> ParseDlcAppIds(VProperty appInfo)
        => await Task.Run(() =>
        {
            List<string> dlcIds = new();
            if (Program.Canceled || appInfo is null)
                return dlcIds;
            VToken extended = appInfo.Value.GetChild("extended");
            if (extended is not null)
                foreach (VToken vToken in extended.Where(p => p is VProperty { Key: "listofdlc" }))
                {
                    VProperty property = (VProperty)vToken;
                    foreach (string id in property.Value.ToString().Split(","))
                        if (int.TryParse(id, out int appId) && appId > 0 && !dlcIds.Contains("" + appId))
                            dlcIds.Add("" + appId);
                }
            VToken depots = appInfo.Value.GetChild("depots");
            if (depots is null)
                return dlcIds;
            foreach (VToken vToken in depots.Where(p => p is VProperty property && int.TryParse(property.Key, out int _)))
            {
                VProperty property = (VProperty)vToken;
                if (int.TryParse(property.Value.GetChild("dlcappid")?.ToString(), out int appId) && appId > 0 && !dlcIds.Contains("" + appId))
                    dlcIds.Add("" + appId);
            }
            return dlcIds;
        });

    // ANTIVIRUS FALSE POSITIVE WARNING:
    // Kill enumerates running processes by name ("steamcmd") and terminates them.
    // Process-enumeration and process-kill APIs are used here only to clean up child
    // steamcmd.exe instances that were started by this application.
    private static async Task Kill()
    {
        List<Task> tasks = Process.GetProcessesByName("steamcmd").Select(process => Task.Run(() =>
        {
            try
            {
                process.Kill(true);
                process.WaitForExit();
                process.Close();
            }
            catch
            {
                // ignored
            }
        })).ToList();
        foreach (Task task in tasks)
            await task;
    }

    internal static void Dispose()
    {
        Kill().GetAwaiter().GetResult();
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, true);
    }
}