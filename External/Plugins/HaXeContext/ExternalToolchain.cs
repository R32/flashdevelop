using System;
using ProjectManager.Projects.Haxe;
using PluginCore;
using System.Diagnostics;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using PluginCore.Managers;
using PluginCore.Bridge;
using ProjectManager.Projects;
using PluginCore.Helpers;
using System.Text.RegularExpressions;

namespace HaXeContext
{
    public class ExternalToolchain
    {
        static string projectPath;
        static WatcherEx watcher;
        static HaxeProject hxproj;
        static MonitorState monitorState;
        static System.Timers.Timer updater;
        static ToolStripComboBoxEx targetBuildSelector;
        static ToolStripComboBoxEx dumpSelector;

        internal static bool HandleProject(IProject project)
        {
            return project is HaxeProject hxproj
                   && hxproj.MovieOptions.HasPlatformSupport
                   && hxproj.MovieOptions.PlatformSupport.ExternalToolchain != null;
        }

        /// <summary>
        /// Run project (after build)
        /// </summary>
        /// <param name="command">Project's custom run command</param>
        /// <returns>Execution handled</returns>
        internal static bool Run(string command)
        {
            if (!string.IsNullOrEmpty(command)) // project has custom run command
                return false;

            var hxproj = PluginBase.CurrentProject as HaxeProject;
            if (!HandleProject(hxproj)) return false;

            var platform = hxproj.MovieOptions.PlatformSupport;
            var toolchain = platform.ExternalToolchain;
            var exe = GetExecutable(toolchain);
            if (exe is null) return false;

            var args = GetCommand(hxproj, "run");
            if (args is null) return false;

            var config = hxproj.TargetBuild;
            if (string.IsNullOrEmpty(config)) config = "flash";
            else if (config.Contains("android")) CheckADB();
            
            if (config.StartsWithOrdinal("html5") && ProjectManager.Actions.Webserver.Enabled && hxproj.RawHXML != null) // webserver
            {
                foreach (var line in hxproj.RawHXML)
                {
                    if (!line.StartsWithOrdinal("-js ")) continue;
                    var p = line.LastIndexOf('/');
                    if (p == -1) break;// for example: -js _
                    var path = line.Substring(3, p - 3).Trim();
                    path = hxproj.GetAbsolutePath(path);
                    ProjectManager.Actions.Webserver.StartServer(path);
                    return true;
                }
            }

            TraceManager.Add(toolchain + " " + args);

            if (hxproj.TraceEnabled && hxproj.EnableInteractiveDebugger) // debugger
            {
                DataEvent de;
                if (config.StartsWithOrdinal("flash"))
                {
                    de = new DataEvent(EventType.Command, "AS3Context.StartProfiler", null);
                    EventManager.DispatchEvent(hxproj, de);
                }
                de = new DataEvent(EventType.Command, "AS3Context.StartDebugger", null);
                EventManager.DispatchEvent(hxproj, de);
            }

            exe = Environment.ExpandEnvironmentVariables(exe);
            if (ShouldCapture(platform.ExternalToolchainCapture, config))
            {
                var oldWD = PluginBase.MainForm.WorkingDirectory;
                PluginBase.MainForm.WorkingDirectory = hxproj.Directory;
                PluginBase.MainForm.CallCommand("RunProcessCaptured", exe + ";" + args);
                PluginBase.MainForm.WorkingDirectory = oldWD;
            }
            else
            {
                var infos = new ProcessStartInfo(exe, args);
                infos.WorkingDirectory = hxproj.Directory;
                infos.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(infos);
            }
            return true;
        }

        static bool ShouldCapture(string[] targets, string config)
        {
            return targets != null && targets.Any(config.StartsWithOrdinal);
        }

        /// <summary>
        /// Start Android ADB server in the background
        /// </summary>
        static void CheckADB()
        {
            if (Process.GetProcessesByName("adb").Length > 0)
                return;

            var adb = Environment.ExpandEnvironmentVariables("%ANDROID_SDK%/platform-tools");
            if (adb.StartsWith('%') || !Directory.Exists(adb))
                adb = Path.Combine(PathHelper.ToolDir, "android/platform-tools");
            if (!Directory.Exists(adb)) return;
            adb = Path.Combine(adb, "adb.exe");
            var p = new ProcessStartInfo(adb, "get-state");
            p.UseShellExecute = true;
            p.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(p);
        }

        internal static bool Clean(IProject project)
        {
            if (!HandleProject(project)) return false;
            var hxproj = (HaxeProject) project;

            var toolchain = hxproj.MovieOptions.PlatformSupport.ExternalToolchain;
            var exe = GetExecutable(toolchain);
            if (exe is null) return false;

            var args = GetCommand(hxproj, "clean");
            if (args is null) return false;

            TraceManager.Add(toolchain + " " + args);

            var pi = new ProcessStartInfo();
            pi.FileName = Environment.ExpandEnvironmentVariables(exe);
            pi.Arguments = args;
            pi.UseShellExecute = false;
            pi.CreateNoWindow = true;
            pi.WorkingDirectory = Path.GetDirectoryName(hxproj.ProjectPath);
            pi.WindowStyle = ProcessWindowStyle.Hidden;
            var p = Process.Start(pi);
            p.WaitForExit(5000);
            p.Close();
            return true;
        }

        [Flags]
        private enum MonitorState
        {
            ProjectSwitch  = 1 << 0,
            ProjectOnSame  = 1 << 1,  // When closing the changed PropertiesDialog
            ProjectUpdate  = 1 << 2,
            WatcherChange  = 1 << 3,
        }
        /// <summary>
        /// Watch NME projects to update the configuration & HXML command using 'nme display'
        /// </summary>
        /// <param name="project"></param>
        public static void Monitor(IProject project)
        {
            if (project is HaxeProject pj)
            {
                if (updater is null)
                {
                    updater = new System.Timers.Timer();
                    updater.Interval = 200;
                    updater.SynchronizingObject = (System.Windows.Forms.Form)PluginBase.MainForm;
                    updater.Elapsed += updater_Elapsed;
                    updater.AutoReset = false;
                }
                if (targetBuildSelector is null)
                {
                    var items = PluginBase.MainForm.ToolStrip.Items.Find("TargetBuildSelector", false);
                    if (items.Length == 1)
                    {
                        targetBuildSelector = items[0] as ToolStripComboBoxEx;
                    }
                }
                if (dumpSelector is null)
                {
                    dumpSelector = new ToolStripComboBoxEx
                    {
                        Name = "DumpSelector",
                        ToolTipText = "Select Dump Mode",
                        AutoSize = false,
                        Width = 60,
                        Margin = new Padding(1, 0, 0, 0),
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        FlatStyle = PluginBase.MainForm.Settings.ComboBoxFlatStyle,
                        Font = PluginBase.Settings.DefaultFont,
                    };
                    dumpSelector.Items.AddRange(DumpConfig.All);
                    dumpSelector.FlatCombo.SelectionChangeCommitted += delegate { hxproj.Dump.Mode = dumpSelector.Text; };
                    PluginBase.MainForm.ToolStrip.Items.Add(dumpSelector);
                    dumpSelector.FlatCombo.AfterTheming();
                }
                else if (!dumpSelector.Visible)
                {
                    dumpSelector.Visible = true;
                }
                monitorState = MonitorState.ProjectSwitch;
                if (hxproj == pj)
                {
                    monitorState |= MonitorState.ProjectOnSame;
                }
                else
                {
                    hxproj = pj;
                    hxproj.ProjectUpdating += hxproj_ProjectUpdating;
                }
                hxproj_ProjectUpdating(hxproj);
            }
            else
            {
                if (dumpSelector != null) dumpSelector.Visible = false;
                StopWatcher();
            }
        }

        internal static void StopWatcher()
        {
            if (watcher is null) return;
            watcher.Dispose();
            watcher = null;
            projectPath = null;
        }

        static void hxproj_ProjectUpdating(Project project)
        {
            if (!HandleProject(project))
            {
                StopWatcher();
                return;
            }
            monitorState |= MonitorState.ProjectUpdate;
            string projectFile = hxproj.OutputPathAbsolute;
            if (projectPath != projectFile)
            {
                StopWatcher();
                projectPath = projectFile;
                if (File.Exists(projectPath))
                {
                    watcher = new WatcherEx(Path.GetDirectoryName(projectPath), Path.GetFileName(projectPath));
                    watcher.Changed += watcher_Changed;
                    watcher.EnableRaisingEvents = true;
                    monitorState |= MonitorState.WatcherChange;
                }
            }
            else if (!monitorState.HasFlag(MonitorState.ProjectSwitch) && hxproj.MovieOptions.Platform == "hxml")
            {
                foreach (var item in hxproj.MultiHXML)
                {
                    if (item.Label == hxproj.TargetBuild)
                    {
                        hxproj.TargetSelect(item);
                        dumpSelector.Text = hxproj.Dump.Mode;
                        break;
                    }
                }
            }
            UpdatePreBuildEvent();
            UpdateProject();
        }

        static void updater_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            monitorState = MonitorState.WatcherChange;
            UpdateProject();
            hxproj.PropertiesChanged();
            UpdateTargetBuildSelector();
        }

        static void watcher_Changed(object sender, FileSystemEventArgs e)
        {
            updater.Enabled = false;
            updater.Enabled = true;
        }

        static void UpdateTargetBuildSelector()
        {
            if (targetBuildSelector != null && hxproj.MovieOptions.Platform == "hxml")
            {
                targetBuildSelector.Items.Clear();
                string[] labels = hxproj.MovieOptions.TargetBuildTypes;
                if (labels != null && labels.Length > 0)
                {
                    targetBuildSelector.Items.AddRange(labels);
                    targetBuildSelector.Text = hxproj.TargetBuild;
                }
                else
                {
                    targetBuildSelector.Text = "";
                }
            }
        }

        private static void UpdatePreBuildEvent()
        {
            var exe = GetExecutable(hxproj.MovieOptions.PlatformSupport.ExternalToolchain);
            if (exe is null) return;
            var args = GetCommand(hxproj, "build", false);
            if (args is null)
            {
                TraceManager.Add($"No external 'build' command found for platform '{hxproj.MovieOptions.Platform}'", -3);
            }
            else
            {
                string prebuild = "";
                if (hxproj.MovieOptions.PlatformSupport.ExternalToolchain == "haxelib") prebuild = "\"$(CompilerPath)/haxelib\" " + args;
                else if (hxproj.MovieOptions.PlatformSupport.ExternalToolchain == "cmd") prebuild = "cmd " + args;
                else prebuild = "\"" + exe + "\" " + args;
                if (hxproj.MovieOptions.Platform == "hxml" && hxproj.OutputType == OutputType.Application)
                {
                    if (hxproj.PreBuildEvent == prebuild) hxproj.PreBuildEvent = "";
                }
                else if (string.IsNullOrEmpty(hxproj.PreBuildEvent))
                {
                    hxproj.PreBuildEvent = prebuild;
                }
            }
            var run = GetCommand(hxproj, "run");
            if (run != null)
            {
                hxproj.OutputType = OutputType.CustomBuild;
                if (hxproj.TestMovieBehavior == TestMovieBehavior.Default)
                {
                    hxproj.TestMovieBehavior = TestMovieBehavior.Custom;
                    hxproj.TestMovieCommand = "";
                }
            }
        }

        private static void UpdateProject()
        {
            var form = (System.Windows.Forms.Form) PluginBase.MainForm;
            if (form.InvokeRequired)
            {
                form.BeginInvoke((System.Windows.Forms.MethodInvoker)UpdateProject);
                return;
            }
            MonitorState state = monitorState;
            monitorState = 0;

            if (hxproj.MovieOptions.Platform == "Lime" && string.IsNullOrEmpty(hxproj.TargetBuild)) return;

            if (hxproj.MovieOptions.Platform == "hxml" && !(hxproj.MultiHXML.Count == 0 || state.HasFlag(MonitorState.WatcherChange))) return;

            var exe = GetExecutable(hxproj.MovieOptions.PlatformSupport.ExternalToolchain);
            if (exe is null) return;

            var args = GetCommand(hxproj, "display");
            if (args is null)
            {
                TraceManager.Add($"No external 'display' command found for platform '{hxproj.MovieOptions.Platform}'", -3);
                return;
            }

            var pi = new ProcessStartInfo();
            pi.FileName = Environment.ExpandEnvironmentVariables(exe);
            pi.Arguments = args;
            pi.RedirectStandardError = true;
            pi.RedirectStandardOutput = true;
            pi.UseShellExecute = false;
            pi.CreateNoWindow = true;
            pi.WorkingDirectory = Path.GetDirectoryName(hxproj.ProjectPath);
            pi.WindowStyle = ProcessWindowStyle.Hidden;
            var p = Process.Start(pi);
            p.WaitForExit(5000);

            var hxml = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.Close();

            if (string.IsNullOrEmpty(hxml) || (!string.IsNullOrEmpty(err) && err.Trim().Length > 0))
            {
                if (string.IsNullOrEmpty(err)) err = "External tool error: no response";
                TraceManager.Add(err, -3);
                hxproj.RawHXML = null;
            }
            else if (hxml.IndexOfOrdinal("not installed") > 0)
            {
                TraceManager.Add(hxml, -3);
                hxproj.RawHXML = null;
            }
            else
            {
                hxml = hxml.Replace("--macro keep", "#--macro keep"); // TODO remove this hack
                hxml = Regex.Replace(hxml, "(-[a-z0-9-]+)\\s*[\r\n]+([^-#])", "$1 $2", RegexOptions.IgnoreCase);
                hxproj.RawHXML = Regex.Split(hxml, "[\r\n]+");

                dumpSelector.Text = hxproj.Dump.Mode;

                if (!state.HasFlag(MonitorState.ProjectOnSame))
                {
                    hxproj.Save();
                }
            }
        }

        static string GetExecutable(string toolchain)
        {
            if (toolchain == "haxelib")
            {
                var haxelib = GetHaxelib(hxproj);
                if (haxelib == "haxelib")
                {
                    TraceManager.Add("haxelib.exe not found in SDK path", -3);
                    return null;
                }
                return haxelib;
            }
            if (toolchain == "cmd")
            {
                return "%SystemRoot%\\system32\\cmd.exe";
            }
            if (File.Exists(toolchain))
            {
                return toolchain;
            }
            return null;
        }

        static string GetHaxelib(IProject project)
        {
            var haxelib = project.CurrentSDK;
            if (haxelib is null) return "haxelib";
            haxelib = Directory.Exists(haxelib)
                ? Path.Combine(haxelib, "haxelib.exe")
                : haxelib.Replace("haxe.exe", "haxelib.exe");

            if (!File.Exists(haxelib)) return "haxelib";

            // fix environment for command line tools
            var sdk = Path.GetDirectoryName(haxelib);
            var de = new DataEvent(EventType.Command, "Context.SetHaxeEnvironment", sdk);
            EventManager.DispatchEvent(null, de);
            
            return haxelib;
        }

        /// <summary>
        /// Get build/run/clean commands
        /// </summary>
        static string GetCommand(Project project, string name) => GetCommand(project, name, true);

        static string GetCommand(Project project, string name, bool processArguments)
        {
            var platform = project.MovieOptions.PlatformSupport;
            var version = platform.GetVersion(project.MovieOptions.Version);
            if (version.Commands is null)
            {
                throw new Exception($"No external commands found for target {project.MovieOptions.Platform} and version {project.MovieOptions.Version}");
            }
            if (!version.Commands.ContainsKey(name)) return null;
            var cmd = version.Commands[name].Value;
            if (platform.ExternalToolchain == "haxelib") cmd = "run " + cmd;
            else if (platform.ExternalToolchain == "cmd") cmd = "/c " + cmd;
                
            if (!processArguments) return cmd;
            return PluginBase.MainForm.ProcessArgString(cmd);
        }
    }
}
