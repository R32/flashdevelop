using System;
using System.IO;
using ProjectManager.Projects.Haxe;
using FDBuild.Building;

namespace ProjectManager.Building.Haxe
{
    public class HaxeProjectBuilder : ProjectBuilder
    {
        HaxeProject project;

        string haxePath;

        public HaxeProjectBuilder(HaxeProject project, string compilerPath)
            : base(project, compilerPath)
        {
            this.project = project;

            string basePath = compilerPath ?? @"C:\Motion-Twin\haxe"; // default installation
            haxePath = Path.Combine(basePath, "haxe.exe");
            if (!File.Exists(haxePath)) 
                haxePath = "haxe.exe"; // hope you have it in your environment path!
        }

        protected override void DoBuild(string[] extraClasspaths, bool noTrace)
        {
            Environment.CurrentDirectory = project.Directory;

            string output = project.FixDebugReleasePath(project.OutputPathAbsolute).TrimEnd('\\', '/');
            if (project.MovieOptions.Platform == "hxml")
            {
                project.MultiHXML.Clear();
                SingleTarget common = new SingleTarget() { Cwd = "." };
                SingleTarget dummy = common;
                bool hasEach = false;
                project.ParseHXMLInner(System.IO.File.ReadAllLines(output), common, ref dummy, ref hasEach, false);
                var label = FDBuild.Program.BuildOptions.TargetBuild;
                output = "";
                foreach (var current in project.MultiHXML)
                {
                    if (current.Label == label)
                    {
                        project.TargetSelect(current, false);
                        project.MovieOptions.Platform = current.Name;
                        output = current.Output;
                        break;
                    }
                }
            }
            string serverPort = Environment.ExpandEnvironmentVariables("%HAXE_SERVER_PORT%");
            string connect = (!serverPort.StartsWith("%", StringComparison.Ordinal) && serverPort != "0")
                ? "--connect " + serverPort : "";

            // always use relative path for CPP (because it prepends ./)
            //if (project.IsCppOutput)
            //    output = project.FixDebugReleasePath(project.OutputPath);

            if (project.IsFlashOutput)
            {
                SwfmillLibraryBuilder libraryBuilder = new SwfmillLibraryBuilder();

                // before doing anything else, make sure any resources marked as "keep updated"
                // are properly kept up to date if possible
                libraryBuilder.KeepUpdated(project);

                // if we have any resources, build our library file and run swfmill on it
                libraryBuilder.BuildLibrarySwf(project, false);
            }
            ;
            if (FDBuild.Program.BuildOptions.DumpMode != null)
            {
                project.CompilerOptions.Directives = Array.FindAll(project.CompilerOptions.Directives, delegate (string item) {
                    return (item.StartsWith("dump") && (item.Length == 4 || (item.Length > 4 && item[4] == '='))) ? false : true;
                });
                project.Dump.Mode = FDBuild.Program.BuildOptions.DumpMode;
            }
            string haxeArgs = connect + " " + String.Join(" ", project.BuildHXML(extraClasspaths, output, noTrace));
            Console.WriteLine("haxe " + haxeArgs);

            if (!ProcessRunner.Run(haxePath, haxeArgs, false, false))
                throw new BuildException("Build halted with errors (haxe.exe).");
        }



        /*private string[] BuildNmeCommand(string[] extraClasspaths, string output, string target, bool noTrace, string extraArgs)
        {
            List<String> pr = new List<String>();

            string builder = HaxeProject.GetBuilder(output);
            if (builder == null) builder = "openfl";

            pr.Add("run " + builder + " build");
            pr.Add(Quote(output));
            pr.Add(target);
            if (!noTrace)
            {
                pr.Add("-debug");
                if (target.StartsWith("flash")) pr.Add("-Dfdb");
            }
            if (extraArgs != null) pr.Add(extraArgs);

            return pr.ToArray();
        }*/
    }
}
