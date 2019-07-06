using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using PluginCore;
using PluginCore.Helpers;

namespace ProjectManager.Projects.Haxe
{
    public class HaxeProject : Project
    {
        // hack : we cannot reference settings HaxeProject is also used by FDBuild
        public static bool saveHXML = false;

        protected string[] rawHXML;

        public List<SingleTarget> MultiHXML { get; private set;}

        public HaxeProject(string path) : base(path, new HaxeOptions())
        {
            movieOptions = new HaxeMovieOptions();
            MultiHXML = new List<SingleTarget>();
        }

        public override string Language => "haxe";
        public override string LanguageDisplayName => "Haxe";
        public override bool IsCompilable => true;
        public override bool ReadOnly => false;
        public override bool HasLibraries => OutputType == OutputType.Application && IsFlashOutput;
        public override bool RequireLibrary => IsFlashOutput;
        public override string DefaultSearchFilter => "*.hx;*.hxp";

        public override string LibrarySWFPath
        {
            get
            {
                var projectName = RemoveDiacritics(Name);
                return Path.Combine("obj", projectName + "Resources.swf");
            }
        }

        public string[] RawHXML
        {
            get => rawHXML;
            set => ParseHXML(value);
        }

        public new HaxeOptions CompilerOptions => (HaxeOptions)base.CompilerOptions;

        public string HaxeTarget => MovieOptions.HasPlatformSupport ? MovieOptions.PlatformSupport.HaxeTarget : null;

        public bool IsFlashOutput => HaxeTarget == "swf";

        public override string GetInsertFileText(string inFile, string path, string export, string nodeType)
        {
            if (export != null) return export;
            var isInjectionTarget = (UsesInjection && path == GetAbsolutePath(InputPath));
            if (IsLibraryAsset(path) && !isInjectionTarget)
                return GetAsset(path).ID;

            var dirName = inFile;
            if (FileInspector.IsHaxeFile(inFile, Path.GetExtension(inFile).ToLower()))
                dirName = ProjectPath;

            return '"' + ProjectPaths.GetRelativePath(Path.GetDirectoryName(dirName), path).Replace('\\', '/') + '"'; 
        }

        public override CompileTargetType AllowCompileTarget(string path, bool isDirectory)
        {
            if (isDirectory || !FileInspector.IsHaxeFile(path, Path.GetExtension(path))) return CompileTargetType.None;

            foreach (string cp in AbsoluteClasspaths)
                if (path.StartsWith(cp, StringComparison.OrdinalIgnoreCase))
                    return CompileTargetType.AlwaysCompile | CompileTargetType.DocumentClass;
            return CompileTargetType.None;
        }

        public override bool IsDocumentClass(string path) 
        {
            foreach (string cp in AbsoluteClasspaths)
                if (path.StartsWith(cp, StringComparison.OrdinalIgnoreCase))
                {
                    string cname = GetClassName(path, cp);
                    if (CompilerOptions.MainClass == cname) return true;
                }
            return false;
        }

        public override void SetDocumentClass(string path, bool isMain)
        {
            if (isMain)
            {
                ClearDocumentClass();
                if (!IsCompileTarget(path)) SetCompileTarget(path, true);
                foreach (string cp in AbsoluteClasspaths)
                    if (path.StartsWith(cp, StringComparison.OrdinalIgnoreCase))
                    {
                        CompilerOptions.MainClass = GetClassName(path, cp);
                        break;
                    }
            }
            else 
            {
                SetCompileTarget(path, false);
                CompilerOptions.MainClass = "";
            }
        }

        private void ClearDocumentClass()
        {
            if (string.IsNullOrEmpty(CompilerOptions.MainClass)) 
                return;

            string docFile = CompilerOptions.MainClass.Replace('.', Path.DirectorySeparatorChar) + ".hx";
            CompilerOptions.MainClass = "";
            foreach (string cp in AbsoluteClasspaths)
            {
                var path = Path.Combine(cp, docFile);
                if (File.Exists(path))
                {
                    SetCompileTarget(path, false);
                    break;
                }
            }
        }

        public override bool Clean()
        {
            try
            {
                if (!string.IsNullOrEmpty(OutputPath) && File.Exists(GetAbsolutePath(OutputPath)))
                {
                    if (MovieOptions.HasPlatformSupport && MovieOptions.PlatformSupport.ExternalToolchain == null)
                        File.Delete(GetAbsolutePath(OutputPath));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        string Quote(string s)
        {
            if (s.IndexOf(' ') >= 0)
                return "\"" + s + "\"";
            return s;
        }

        public string[] BuildHXML(string[] paths, string outfile, bool release)
        {
            var pr = new List<string>();
            var isFlash = IsFlashOutput;

            if (rawHXML != null)
            {
                pr.AddRange(rawHXML);
            }
            else
            {
                // SWC libraries
                if (isFlash)
                    foreach (LibraryAsset asset in LibraryAssets)
                    {
                        if (asset.IsSwc)
                            pr.Add("-swf-lib " + asset.Path);
                    }

                // libraries
                foreach (var lib in CompilerOptions.Libraries)
                    if (lib.Length > 0)
                    {
                        if (lib.Trim().StartsWith("-lib", StringComparison.Ordinal)) pr.Add(lib);
                        else pr.Add("-lib " + lib);
                    }

                // class paths
                var classPaths = paths.ToList();
                classPaths.AddRange(Classpaths);
                foreach (var cp in classPaths)
                {
                    var ccp = string.Join("/", cp.Split('\\'));
                    pr.Add("-cp " + Quote(ccp));
                }

                // compilation mode
                var mode = HaxeTarget;
                //throw new SystemException("Unknown mode");

                if (mode != null)
                {
                    outfile = string.Join("/", outfile.Split('\\'));
                    pr.Add("-" + mode + " " + Quote(outfile));
                }

                // flash options
                if (isFlash)
                {
                    var htmlColor = MovieOptions.Background.Substring(1);
                    if (htmlColor.Length > 0)
                        htmlColor = ":" + htmlColor;

                    pr.Add("-swf-header " + $"{MovieOptions.Width}:{MovieOptions.Height}:{MovieOptions.Fps}{htmlColor}");

                    if (!UsesInjection && LibraryAssets.Count > 0)
                        pr.Add("-swf-lib " + Quote(LibrarySWFPath));

                    if (CompilerOptions.FlashStrict)
                        pr.Add("--flash-strict");

                    // haxe compiler uses Flash version directly
                    var version = MovieOptions.Version;
                    if (version != null) pr.Add("-swf-version " + version);
                }

                // defines
                foreach (var def in CompilerOptions.Directives)
                    pr.Add("-D " + Quote(def));

                // add project files marked as "always compile"
                foreach (var relTarget in CompileTargets)
                {
                    var absTarget = GetAbsolutePath(relTarget);
                    // guess the class name from the file name
                    foreach (var cp in classPaths)
                        if (absTarget.StartsWith(cp, StringComparison.OrdinalIgnoreCase))
                        {
                            var className = GetClassName(absTarget, cp);
                            if (CompilerOptions.MainClass != className)
                                pr.Add(className);
                        }
                }

                // add main class
                if (!string.IsNullOrEmpty(CompilerOptions.MainClass))
                    pr.Add("-main " + CompilerOptions.MainClass);
                
                // extra options
                foreach (var opt in CompilerOptions.Additional)
                {
                    var p = opt.Trim();
                    if (p == "" || p[0] == '#') continue;
                    char[] space = { ' ' };
                    var parts = p.Split(space, 2);
                    if (parts.Length == 1) pr.Add(p);
                    else pr.Add(parts[0] + ' ' + Quote(parts[1]));
                }
            }

            // debug
            if (!release)
            {
                pr.Insert(0, "-debug");
                if (CurrentSDK == null || !CurrentSDK.Contains("Motion-Twin")) // Haxe 3+
                    pr.Insert(1, "--each");
                if (isFlash && EnableInteractiveDebugger && CompilerOptions.EnableDebug)
                {
                    pr.Insert(1, "-D fdb");
                    if (CompilerOptions.NoInlineOnDebug)
                        pr.Insert(2, "--no-inline");
                }
            }
            return pr.ToArray();
        }

        private string GetClassName(string absTarget, string cp)
        {
            var className = absTarget.Substring(cp.Length);
            className = className.Substring(0, className.LastIndexOf('.'));
            className = Regex.Replace(className, "[\\\\/]+", ".");
            if (className.StartsWith(".", StringComparison.Ordinal)) className = className.Substring(1);
            return className;
        }

        #region Load/Save

        public static HaxeProject Load(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            if (ext == ".hxml")
            {
                return new HaxeProject(path) {RawHXML = File.ReadAllLines(path)};
            }

            var reader = new HaxeProjectReader(path);

            try
            {
                return reader.ReadProject();
            }
            catch (XmlException e)
            {
                var format = $"Error in XML Document line {e.LineNumber}, position {e.LinePosition}.";
                throw new Exception(format, e);
            }
            finally { reader.Close(); }
        }

        public override void Save() => SaveAs(ProjectPath);

        public override void SaveAs(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            if (ext != ".hxproj") return;

            if (!AllowedSaving(fileName)) return;
            try
            {
                var writer = new HaxeProjectWriter(this, fileName);
                writer.WriteProject();
                writer.Flush();
                writer.Close();
                if (saveHXML && movieOptions.Platform != "hxml" && OutputType != OutputType.CustomBuild) {
                    StreamWriter hxml = File.CreateText(Path.ChangeExtension(fileName, "hxml"));
                    foreach( string e in BuildHXML(new string[0],this.OutputPath,true) )
                        hxml.WriteLine(e);
                    hxml.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "IO Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region HXML parsing

        private void ParseHXML(string[] raw)
        {
            if (raw != null && (raw.Length == 0 || raw[0] is null))
                raw = null;
            rawHXML = raw;
            MultiHXML.Clear();
            SingleTarget common = new SingleTarget() { Cwd = "." };
            SingleTarget current = common;
            bool hasEach = false;
            if (raw != null) ParseHXMLInner(raw, common, ref current, ref hasEach, false);
            TargetSelect(current);
        }

        public void TargetSelect(SingleTarget current)
        {
            string[] labels = new string[MultiHXML.Count];
            int i = 0;
            foreach (var item in MultiHXML)
            {
                labels[i++] = item.Label;
                if (TargetBuild == item.Label) current = item;
            }
            if (i > 0 && current.Label != TargetBuild) current = MultiHXML[0];

            CompilerOptions.MainClass = current.MainClass;
            CompilerOptions.Directives = current.Defs.ToArray();
            CompilerOptions.Libraries = current.Libs.ToArray();
            CompilerOptions.Additional = current.Adds.ToArray();
            if (current.Cps.Count == 0) current.Cps.Add(".");
            Classpaths.Clear();
            Classpaths.AddRange(current.Cps);

            if (MovieOptions.HasPlatformSupport)
            {
                var platform = MovieOptions.PlatformSupport;
                if (platform.Name == "hxml")
                {
                    MovieOptions.TargetBuildTypes = labels;
                    TargetBuild = current.Label ?? "";
                }
                else
                {
                    MovieOptions.TargetBuildTypes = platform.Targets;
                    TargetBuild = "";
                }
            }
            else
            {
                MovieOptions.TargetBuildTypes = null;
            }

            if (MovieOptions.TargetBuildTypes is null)
            {
                OutputPath = current.Output;
                OutputType = OutputType.Application;
                MovieOptions.Platform = FindPlatform(current.Target).Name;
            }
        }

        private static readonly Regex reHxOp = new Regex("^-([a-z0-9-]+)\\s*(.*)", RegexOptions.IgnoreCase);

        public void ParseHXMLInner(string[] lines, SingleTarget common, ref SingleTarget current, ref bool hasEach, bool isSub)
        {

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                Match m = reHxOp.Match(trimmedLine);
                if (m.Success)
                {
                    string op = m.Groups[1].Value;
                    if (op == "-each")
                    {
                        if (!hasEach)
                        {
                            hasEach = true;
                            current = common.Duplicate();
                        }
                        continue;
                    }
                    string value = m.Groups[2].Value.Trim();
                    switch (op)
                    {
                        case "D": case "-define":
                            current.Defs.Add(value);
                            break;
                        case "p": case "cp":
                            current.Cps.Add(CleanPath(value, current.Cwd));
                            break;
                        case "L": case "lib":
                            current.Libs.Add(value);
                            break;
                        case "m": case "main": case "-main":
                            current.MainClass = value;
                            break;
                        case "-next":
                            current.SafelyInsertTo(MultiHXML);
                            current = hasEach ? common.Duplicate() : new SingleTarget() { Cwd = "." };
                            break;
                        case "-connect": case "-wait":
                            break;
                        case "-cwd":
                            current.Cwd = this.CleanPath(value, current.Cwd);
                            break;
                        default:
                            var targetPlatform = FindPlatform(op);
                            if (targetPlatform is null)
                            {
                                current.Adds.Add(trimmedLine);
                            }
                            else if (string.IsNullOrEmpty(current.Target))
                            {
                                current.Target = targetPlatform.HaxeTarget;
                                current.Name = targetPlatform.Name;
                                current.Label = targetPlatform.Name;
                                current.Output = value;
                            }
                            break;
                    }
                }
                else if (!trimmedLine.StartsWith("#"))
                {
                    if (trimmedLine.EndsWith(".hxml", StringComparison.OrdinalIgnoreCase))
                    {
                        string subhxml = this.GetAbsolutePath(CleanPath(trimmedLine, current.Cwd));
                        if (File.Exists(subhxml))
                        {
                            ParseHXMLInner(File.ReadAllLines(subhxml), common, ref current, ref hasEach, true);
                        }
                    }
                    else
                    {
                        current.Adds.Add(trimmedLine);
                    }
                }
            }
            if (!isSub)
            {
                current.SafelyInsertTo(MultiHXML);
                // checking if duplicate
                var dict = new Dictionary<string, int>();
                var count = 0;
                foreach (var item in MultiHXML)
                    dict[item.Label] = dict.TryGetValue(item.Label, out count) ? count + 1 : 1;

                foreach (var item in MultiHXML)
                    if (dict.TryGetValue(item.Label, out count) && count > 1)
                        item.Label = item.Target.ToUpper() + " " + Path.GetFileName(item.Output);
            }
        }

        private LanguagePlatform FindPlatform(string op)
        {
            if (op[0] == '-' && op != "-interp")
            {
                op = op.Substring(1);
            }
            var lang = PlatformData.SupportedLanguages["haxe"];
            foreach (var platform in lang.Platforms.Values)
            {
                if (platform.HaxeTarget == op) return platform;
            }
            return null;
        }

        private string CleanPath(string path, string cwd)
        {
            path = path.Replace("\"", string.Empty);
            path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            // handle if NME/OpenFL config file is not at the root of the project directory
            if (Path.IsPathRooted(path)) return path;
            
            var relDir = Path.GetDirectoryName(ProjectPath);
            var absPath = Path.GetFullPath(Path.Combine(relDir, cwd, path));
            return GetRelativePath(absPath);
        }

        #endregion
    }

    #region SingleTarget
    public class SingleTarget
    {
        public string Label { get; internal set; }      // Used to distinguish if there are multiple equal "Names"
        public string Name { get; internal set; }       // Same as Platform.Name which is used for FBuild to get HaxeTarget
        public string Target { get; internal set; }     // Same as Platform.HaxeTarget. e.g: js, hl, swf, cpp, ....
        public string Output { get; internal set; }
        public string MainClass { get; internal set; }
        public string Cwd { get; internal set; }
        public List<string> Cps { get; internal set; }
        public List<string> Libs { get; internal set; }
        public List<string> Defs { get; internal set; }
        public List<string> Adds { get; internal set; }

        internal SingleTarget()
        {
            MainClass = "";
            Cps = new List<string>();
            Libs = new List<string>();
            Defs = new List<string>();
            Adds = new List<string>();
        }

        internal void LabelHack()
        {
            if (Target == "hl" && Output.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
            {
                Label = "HL C";
            }
            else if (Target == "swf" && Output.EndsWith(".swc", StringComparison.OrdinalIgnoreCase))
            {
                Label = "Flash SWC";
            }
        }

        internal void SafelyInsertTo(List<SingleTarget> list)
        {
            if (!string.IsNullOrEmpty(this.Target))
            {
                LabelHack();
                list.Add(this);
            }
        }
        // Clone without .Target
        internal SingleTarget Duplicate()
        {
            SingleTarget ret = new SingleTarget() { MainClass = this.MainClass, Cwd = this.Cwd, };
            ret.Cps.AddRange(this.Cps);
            ret.Libs.AddRange(this.Libs);
            ret.Defs.AddRange(this.Defs);
            ret.Adds.AddRange(this.Adds);
            return ret;
        }
    }
    #endregion
}
