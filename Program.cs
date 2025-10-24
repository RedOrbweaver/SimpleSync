using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace SimpleSync
{
    static class SimpleSync
    {
        public class KnownFile
        {
            public required string name;
            public required string source_path;
            public required string destination_path;
            public required int last_sync_existence;
            public required byte[] hash;
        }
        public class KnownDirectory
        {
            public required string relative_path;
            public required string source_path;
            public required string destination_path;
            public List<KnownFile> files = [];
            public Dictionary<string, KnownFile> files_by_name = [];
            public List<KnownDirectory> directories = [];
            public Dictionary<string, KnownDirectory> directories_by_name = [];
            public required int last_sync_existence;
        }
        public class Statistics
        {
            public int files_checked = 0;
            public int files_changed = 0;
            public int files_overriden = 0;
            public int files_created = 0;
            public int directories_checked = 0;
            public int directories_created = 0;
            public long bytes_checked = 0;
            public long bytes_written = 0;
        }
        public static int sync_count = 0;
        public static bool has_log_output = false;
        public static string log_file = "";
        public static string source = "";
        public static string destination = "";
        public static ManualResetEvent cancel_event = new(false);
        public static KnownDirectory root = null;
        public static Statistics statistics = new ();
        public static void LogLine(string message)
        {
            Console.WriteLine(message);
            if (has_log_output)
            {
                try
                {
                    File.AppendAllText(log_file, message + "\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to access the log file {log_file}, reason: {ex.Message}");
                    throw;
                }
            }
        }
        public static void DeleteDirectory(string path)
        {
            LogLine($"Deleting directory {path}");
            Directory.GetDirectories(path).ToList().ForEach(DeleteDirectory);
            Directory.GetFiles(path).ToList().ForEach(it =>
            {
                LogLine($"Deleting file: {it}");
                File.Delete(it);
            });
            Directory.Delete(path);
        }
        public static void SynchronizeFile(KnownFile kfile)
        {
            statistics.files_checked++;
            long size = new FileInfo(kfile.source_path).Length;
            statistics.bytes_checked += size;
            FileStream fs;
            try
            {
                fs = File.Open(kfile.source_path, FileMode.Open, FileAccess.Read);
            }
            catch (UnauthorizedAccessException)
            {
                LogLine($"ERROR: Cannot access file {kfile.source_path}: ACCESS DENIED");
                return;
            }
            catch (Exception ex)
            {
                LogLine($"ERROR: Cannot access file {kfile.source_path}: UNKNOWN ERROR ({ex.Message})");
                return;
            }
            var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(fs);
            fs.Close();
            if (File.Exists(kfile.destination_path))
            {
                if (kfile.hash.Length == 0)
                {
                    kfile.hash = hash;
                    fs = File.Open(kfile.destination_path, FileMode.Open, FileAccess.Read);
                    var nhash = md5.ComputeHash(fs);
                    fs.Close();
                    if (kfile.hash.SequenceEqual(nhash))
                    {
                        Console.WriteLine("Hash matches last known value. No changes detected.");
                        return;
                    }
                }
                else if (kfile.hash.SequenceEqual(hash))
                {
                    Console.WriteLine("Hash matches last known value. No changes detected.");
                    return;
                }
                statistics.files_overriden++;
                LogLine($"Overriding file {kfile.destination_path}");
            }
            else
            {
                statistics.files_created++;
                LogLine($"Creating file {kfile.destination_path}");
            }
            File.Copy(kfile.source_path, kfile.destination_path, true);
            kfile.hash = hash;
            statistics.bytes_written += size;
            statistics.files_changed++;
        }
        public static TimeSpan SynchronizeDirectory(KnownDirectory kdir)
        {
            statistics.directories_checked++;
            Stopwatch stopwatch = new();
            stopwatch.Start();
            string[] files = [];
            string[] directories = [];
            try
            {
                files = Directory.GetFiles(kdir.source_path);
                directories = Directory.GetDirectories(kdir.source_path);
            }
            catch (UnauthorizedAccessException)
            {
                LogLine($"ERROR: Failed to access {kdir.source_path}: ACCESS DENIED");
                return stopwatch.Elapsed;
            }
            catch (Exception ex)
            {
                LogLine($"ERROR: Failed to access {kdir.source_path}: UNKNOWN ERROR ({ex.Message})");
                return stopwatch.Elapsed;
            }

            foreach (string file in files)
            {
                if (cancel_event.WaitOne(0))
                {
                    LogLine($"CTRL+C pressed, exiting gracefully before finishing the current sync");
                }
                string name = Path.GetFileName(file);
                KnownFile kfile;
                if (!kdir.files_by_name.TryGetValue(name, out kfile))
                {
                    Console.WriteLine($"New file found: {file}");
                    kfile = new KnownFile() { name = name, source_path = file, destination_path = Path.Combine(kdir.destination_path, name), hash = [], last_sync_existence = sync_count };
                    kdir.files_by_name.Add(name, kfile);
                    kdir.files.Add(kfile);
                }
                else
                {
                    kfile.last_sync_existence = sync_count;
                }
                SynchronizeFile(kfile);
            }
            List<KnownFile> files_to_remove = [];
            foreach (KnownFile kfile in kdir.files)
            {
                if (kfile.last_sync_existence != sync_count)
                {
                    files_to_remove.Add(kfile);
                }
            }
            foreach (KnownFile kfile in files_to_remove)
            {
                LogLine($"Deleting file: {Path.GetRelativePath(source, kfile.source_path)}");
                try
                {
                    File.Delete(kfile.destination_path);
                }
                catch (UnauthorizedAccessException)
                {
                    LogLine($"ERROR: Unable to delete file ({kfile.destination_path}): ACCESS DENIED. Exiting.");
                    throw new Exception($"Failed to delete file {kfile.destination_path}");
                }
                catch (Exception ex)
                {
                    LogLine($"ERROR: Unable to delete file ({kfile.destination_path}): UNKNOWN ERROR ({ex.Message}). Exiting.");
                    throw new Exception($"Failed to delete file {kfile.destination_path}");
                }
                kdir.files.Remove(kfile);
                kdir.files_by_name.Remove(kfile.name);
            }

            foreach (string directory in directories)
            {
                KnownDirectory okdir;
                string name = Path.GetFileName(directory);
                if (!kdir.directories_by_name.TryGetValue(name, out okdir))
                {
                    Console.WriteLine($"New directory found: {directory}");
                    okdir = new KnownDirectory() { relative_path = Path.Combine(kdir.relative_path, name), source_path = directory, destination_path = Path.Combine(kdir.destination_path, kdir.relative_path, name), last_sync_existence = sync_count };
                    kdir.directories_by_name.Add(name, okdir);
                    kdir.directories.Add(okdir);
                    if (!Directory.Exists(okdir.destination_path))
                    {
                        LogLine($"Creating directory: {okdir.destination_path}");
                        Directory.CreateDirectory(okdir.destination_path);
                    }
                    statistics.directories_created++;
                }
                else
                {
                    okdir.last_sync_existence = sync_count;
                }
                TimeSpan time = SynchronizeDirectory(okdir);
                Console.WriteLine($"Finished synchronizing directory {okdir.relative_path} in {time.TotalMilliseconds} milliseconds");
            }

            List<KnownDirectory> directories_to_remove = [];
            foreach (KnownDirectory okdir in kdir.directories)
            {
                if (okdir.last_sync_existence != sync_count)
                {
                    directories_to_remove.Add(okdir);
                }
            }

            foreach (KnownDirectory okdir in directories_to_remove)
            {
                LogLine($"Deleting directory: {okdir.relative_path}");
                try
                {
                    DeleteDirectory(okdir.destination_path);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogLine($"ERROR: Failed to delete directory: ACCESS DENIED ({ex.Message})");
                    throw;
                }
                catch (Exception ex)
                {
                    LogLine($"ERROR: Failed to delete directory: UNKNOWN ERROR ({ex.Message})");
                    throw;
                }
                kdir.directories.Remove(okdir);
                kdir.directories_by_name.Remove(Path.GetFileName(okdir.source_path));
            }

            return stopwatch.Elapsed;
        }
        public static int Main(string[] args)
        {

            if (args.Length <= 1)
            {
                if (args.Length == 0 || args[0].ToLower() == "--help" || args[0].ToLower() == "help")
                {
                    Console.WriteLine("Simplesync - Synchronize files from a source directory to a destination directory (one way).");
                    Console.WriteLine("Usage: SimpleSync <source directory> <destination directory> <check interval in seconds> <(optional) log file>");
                    Console.WriteLine("Relative directories are supported, but should be used with care");
                    Console.WriteLine("A valid interval is any floating point value greater than 0");
                    Console.WriteLine("Remarks:");
                    Console.WriteLine("If a file in the destination directory is modified after the sync, it will not be overriden unless the source file changes or the program is restarted.");
                    Console.WriteLine("The synchronizaton time is not counted towards the interval, so in practice the real interval is (synchronization time) + interval");
                    Console.WriteLine("Access denied on the side of the source should not cause any issues, however if access is denied on the destination side the program will exit with an error");
                    return 0;
                }
            }

            Console.WriteLine("Starting SimpleSync with arguments:");
            args.ToList().ForEach(Console.WriteLine);
            Console.WriteLine("");

            if (args.Length < 3)
            {
                Console.WriteLine("Insufficient number of arguments. See SimpleSync --help for usage.");
                return 1;
            }
            if (args.Length > 4)
            {
                Console.WriteLine("Too many arguments. See SimpleSync --help for usage");
                return 1;
            }

            source = args[0];
            if (source == "")
            {
                Console.WriteLine("Source cannot be an empty string");
            }
            try
            {
                if (!Directory.Exists(source))
                {
                    Console.WriteLine($"Directory {source} does not exist");
                    return 1;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"Cannot access directory {source}: access denied");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cannot access directory {source}: unknown error {ex.Message}");
            }

            destination = args[1];
            if (destination == "")
            {
                Console.WriteLine("Destination cannot be an empty string");
            }
            try
            {
                if (!Directory.Exists(destination))
                {
                    Console.WriteLine($"Directory {destination} does not exist");
                    return 1;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"Cannot access directory {destination}: access denied");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cannot access directory {destination}: unknown error {ex.Message}");
            }

            float interval;
            if (!float.TryParse(args[2], out interval))
            {
                Console.WriteLine("Interval is not a valid floating point value");
                return 1;
            }
            if (interval <= 0.0f)
            {
                Console.WriteLine("Inteval value must be greater than 0");
                return 1;
            }

            if (args.Length == 4)
            {
                has_log_output = true;
                log_file = args[3];
                try
                {
                    if (File.Exists(log_file))
                    {
                        File.Delete(log_file);
                    }
                    File.Create(log_file).Close();
                }
                catch (UnauthorizedAccessException)
                {
                    Console.WriteLine($"Unable to create log file at {log_file}: Access denied");
                    return 1;
                }
                catch (DirectoryNotFoundException)
                {
                    Console.WriteLine($"Unable to create log file at {log_file}: Directory not found");
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unable to create log file at {log_file}: Unknown error ({ex.Message})");
                    return 1;
                }
                Console.WriteLine($"Outputting logs to {log_file}");
            }

            LogLine($"Synchronizing from {source} into {destination} every {interval} seconds");

            Console.CancelKeyPress += delegate (object? sender, ConsoleCancelEventArgs args)
            {

                if (cancel_event.WaitOne(0))
                {
                    LogLine("Exitting forcefully");
                    args.Cancel = false;
                    return;
                }
                args.Cancel = true;
                cancel_event.Set();
                Console.WriteLine("CTRL+C pressed, closing after operations complete. Press again to force-quit.");
            };
            TimeSpan interval_span = TimeSpan.FromSeconds(interval);
            root = new KnownDirectory() { relative_path = "", source_path = source, destination_path = destination, files = [], files_by_name = [], last_sync_existence = 0 };            
            while (true)
            {
                if (cancel_event.WaitOne(0))
                {
                    LogLine("CTRL+C pressed, exiting gracefully");
                    return 0;
                }
                statistics = new Statistics();
                // Note that the time spent synchronizing does not count, so the real interval between syncs will be greater than commanded
                LogLine($"Starting sync #${sync_count} {source} -> {destination} at {DateTime.Now.ToLongDateString()} {DateTime.Now.ToLongTimeString()}");
                TimeSpan time;
                try
                {
                    time = SynchronizeDirectory(root);
                }
                catch (IOException ex)
                {
                    LogLine($"CRITICAL: IO error detected, exiting. Error message: {ex.Message}");
                    return 1;
                }
                LogLine($"Sync #{sync_count} complete in {Math.Round(time.TotalSeconds, 3)} seconds!");
                if (time.TotalSeconds > interval)
                {
                    Console.WriteLine("WARNING: Total synchronization time exceeded synchronization interval!");
                }
                else if (time.TotalSeconds >= interval * 0.5)
                {
                    Console.WriteLine("WARNING: Total synchronization time is more than half of the synchronizaton interval");
                }
                Console.WriteLine($"Checked {statistics.files_checked} files for {statistics.bytes_checked} bytes in {statistics.directories_checked} directories");
                Console.WriteLine($"Created {statistics.files_created} files and overrode {statistics.files_overriden} files");
                Console.WriteLine($"Wrote {statistics.bytes_written} in {statistics.files_changed} files.");
                Console.WriteLine("");
                sync_count++;
                Thread.Sleep(interval_span);
            }
        }
    }
}
