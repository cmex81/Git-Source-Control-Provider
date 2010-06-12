using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using Microsoft.Win32;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio;

using MsVsShell = Microsoft.VisualStudio.Shell;
using ErrorHandler = Microsoft.VisualStudio.ErrorHandler;
using Microsoft.VisualStudio.Shell;
using System.IO;
using System.Collections.Generic;

namespace GitScc
{
    /////////////////////////////////////////////////////////////////////////////
    // BasicSccProvider
    [MsVsShell.ProvideLoadKey("Standard", "0.1", "Git Source Control Provider", "Yiyisun@hotmail.com", 15261)]
    [MsVsShell.DefaultRegistryRoot("Software\\Microsoft\\VisualStudio\\9.0Exp")]
    // Register the package to have information displayed in Help/About dialog box
    [MsVsShell.InstalledProductRegistration(false, "#100", "#101", "1.0.0.0", IconResourceID = CommandId.iiconProductIcon)]
    // Declare that resources for the package are to be found in the managed assembly resources, and not in a satellite dll
    [MsVsShell.PackageRegistration(UseManagedResourcesOnly = true)]
    // Register the resource ID of the CTMENU section (generated from compiling the VSCT file), so the IDE will know how to merge this package's menus with the rest of the IDE when "devenv /setup" is run
    // The menu resource ID needs to match the ResourceName number defined in the csproj project file in the VSCTCompile section
    // Everytime the version number changes VS will automatically update the menus on startup; if the version doesn't change, you will need to run manually "devenv /setup /rootsuffix:Exp" to see VSCT changes reflected in IDE
    [MsVsShell.ProvideMenuResource(1000, 1)]
    // Register a sample options page visible as Tools/Options/SourceControl/SampleOptionsPage when the provider is active
    [MsVsShell.ProvideOptionPageAttribute(typeof(SccProviderOptions), "Source Control", "Git Source Control Provider Options", 106, 107, false)]
    [ProvideToolsOptionsPageVisibility("Source Control", "Git Source Control Provider Options", "C4128D99-0000-41D1-A6C3-704E6C1A3DE2")]

    // Register a sample tool window visible only when the provider is active
    //[MsVsShell.ProvideToolWindow(typeof(SccProviderToolWindow))]
    //[MsVsShell.ProvideToolWindowVisibility(typeof(SccProviderToolWindow), "ADC98052-0000-41D1-A6C3-704E6C1A3DE2")]
    // Register the source control provider's service (implementing IVsScciProvider interface)
    [MsVsShell.ProvideService(typeof(SccProviderService), ServiceName = "Git Source Control Service")]
    // Register the source control provider to be visible in Tools/Options/SourceControl/Plugin dropdown selector
    [ProvideSourceControlProvider("Git Source Control Provider", "#100")]
    // Pre-load the package when the command UI context is asserted (the provider will be automatically loaded after restarting the shell if it was active last time the shell was shutdown)
    [MsVsShell.ProvideAutoLoad("C4128D99-0000-41D1-A6C3-704E6C1A3DE2")]
    // Declare the package guid
    [Guid("C4128D99-2000-41D1-A6C3-704E6C1A3DE2")]
    public class BasicSccProvider : MsVsShell.Package, IOleCommandTarget
    {
        private List<GitFileStatusTracker> projects;
        private SccProviderService sccService = null;

        public BasicSccProvider()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering constructor for: {0}", this.ToString()));
        }

        /////////////////////////////////////////////////////////////////////////////
        // BasicSccProvider Package Implementation
        #region Package Members

        protected override void Initialize()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering Initialize() of: {0}", this.ToString()));
            base.Initialize();

            projects = new List<GitFileStatusTracker>();
            sccService = new SccProviderService(this, projects);

            ((IServiceContainer)this).AddService(typeof(SccProviderService), sccService, true);

            // Add our command handlers for menu (commands must exist in the .vsct file)
            MsVsShell.OleMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as MsVsShell.OleMenuCommandService;
            if (mcs != null)
            {
                CommandID cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdSccCommandRefresh);
                var menu = new MenuCommand(new EventHandler(OnRefreshCommand), cmd);
                mcs.AddCommand(menu);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdSccCommandGitBash);
                menu = new MenuCommand(new EventHandler(OnGitBashCommand), cmd);
                mcs.AddCommand(menu);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdSccCommandGitExtension);
                menu = new MenuCommand(new EventHandler(OnGitExtensionCommand), cmd);
                mcs.AddCommand(menu);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdSccCommandCompare);
                menu = new MenuCommand(new EventHandler(OnCompareCommand), cmd);
                mcs.AddCommand(menu);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdSccCommandUndo);
                menu = new MenuCommand(new EventHandler(OnUndoCommand), cmd);
                mcs.AddCommand(menu);

            }

            // Register the provider with the source control manager
            // If the package is to become active, this will also callback on OnActiveStateChange and the menu commands will be enabled
            IVsRegisterScciProvider rscp = (IVsRegisterScciProvider)GetService(typeof(IVsRegisterScciProvider));
            rscp.RegisterSourceControlProvider(GuidList.guidSccProvider);
        }

        protected override void Dispose(bool disposing)
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering Dispose() of: {0}", this.ToString()));

            base.Dispose(disposing);
        }

        #endregion

        #region menu commands
        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            OLECMDF cmdf = OLECMDF.OLECMDF_SUPPORTED;

            // All source control commands needs to be hidden and disabled when the provider is not active
            if (!sccService.Active)
            {
                cmdf = cmdf | OLECMDF.OLECMDF_INVISIBLE;
                cmdf = cmdf & ~(OLECMDF.OLECMDF_ENABLED);

                prgCmds[0].cmdf = (uint)cmdf;
                return VSConstants.S_OK;
            }


            // Process our Commands
            switch (prgCmds[0].cmdID)
            {
                case CommandId.imnuFileSourceControlMenu:
                    OLECMDTEXT cmdtxtStructure = (OLECMDTEXT)Marshal.PtrToStructure(pCmdText, typeof(OLECMDTEXT));
                    if (cmdtxtStructure.cmdtextf == (uint)OLECMDTEXTF.OLECMDTEXTF_NAME)
                    {
                        string menuText = string.IsNullOrEmpty(sccService.CurrentBranchName) ?
                            "Git" : "Git (" + sccService.CurrentBranchName + ")";

                        SetOleCmdText(pCmdText, menuText);
                    }
                    break;

                case CommandId.icmdSccCommandGitBash:
                    var gitBashPath = GitSccOptions.Current.GitBashPath;
                    if (!string.IsNullOrEmpty(gitBashPath) && File.Exists(gitBashPath))
                    {
                        cmdf |= OLECMDF.OLECMDF_ENABLED;
                    }
                    break;

                case CommandId.icmdSccCommandGitExtension:
                    var gitExtensionPath = GitSccOptions.Current.GitExtensionPath;
                    if (!string.IsNullOrEmpty(gitExtensionPath) && File.Exists(gitExtensionPath))
                    {
                        cmdf |= OLECMDF.OLECMDF_ENABLED;
                    }
                    break;
                
                case CommandId.icmdSccCommandUndo:
                case CommandId.icmdSccCommandCompare:
                    if (sccService.CanCompareSelectedFile) cmdf |= OLECMDF.OLECMDF_ENABLED;
                    break;

                default:
                    cmdf |= OLECMDF.OLECMDF_ENABLED;
                    break;
            }


            prgCmds[0].cmdf = (uint) (cmdf);
            return VSConstants.S_OK;
        }

        public void SetOleCmdText(IntPtr pCmdText, string text)
        {
            OLECMDTEXT CmdText = (OLECMDTEXT)Marshal.PtrToStructure(pCmdText, typeof(OLECMDTEXT));
            char[] buffer = text.ToCharArray();
            IntPtr pText = (IntPtr)((long)pCmdText + (long)Marshal.OffsetOf(typeof(OLECMDTEXT), "rgwz"));
            IntPtr pCwActual = (IntPtr)((long)pCmdText + (long)Marshal.OffsetOf(typeof(OLECMDTEXT), "cwActual"));
            // The max chars we copy is our string, or one less than the buffer size,
            // since we need a null at the end.
            int maxChars = (int)Math.Min(CmdText.cwBuf - 1, buffer.Length);
            Marshal.Copy(buffer, 0, pText, maxChars);
            // append a null
            Marshal.WriteInt16((IntPtr)((long)pText + (long)maxChars * 2), (Int16)0);
            // write out the length + null char
            Marshal.WriteInt32(pCwActual, maxChars + 1);
        }

        private void OnRefreshCommand(object sender, EventArgs e)
        {
            sccService.Refresh();
        }

        private void OnCompareCommand(object sender, EventArgs e)
        {
            sccService.CompareSelectedFile();
        }

        private void OnUndoCommand(object sender, EventArgs e)
        {
            sccService.UndoSelectedFile();
        }

        private void OnGitBashCommand(object sender, EventArgs e)
        {
            var gitBashPath = GitSccOptions.Current.GitBashPath;
            RunDetatched("cmd.exe", string.Format("/c \"{0}\" --login -i", gitBashPath));
        }

        private void OnGitExtensionCommand(object sender, EventArgs e)
        {
            var gitExtensionPath = GitSccOptions.Current.GitExtensionPath;
            RunDetatched(gitExtensionPath, "");
        }

        internal void RunDiffCommand(string file1, string file2)
        {
            var difftoolPath = GitSccOptions.Current.DifftoolPath;
            RunCommand(difftoolPath, "\"" + file1 + "\" \"" + file2 + "\"");
        }
        #endregion

        // This function is called by the IVsSccProvider service implementation when the active state of the provider changes
        // The package needs to show or hide the scc-specific commands 
        public virtual void OnActiveStateChange()
        {
        }

        public new Object GetService(Type serviceType)
        {
            return base.GetService(serviceType);
        }

        #region Run Command
        internal string RunCommand(string cmd, string args)
        {
            var pinfo = new ProcessStartInfo(cmd)
            {
                Arguments = args,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = sccService.CurrentGitWorkingDirectory ??
                    Path.GetDirectoryName(sccService.GetSolutionFileName())
            };

            using (var process = Process.Start(pinfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                    throw new Exception(error);

                return output;
            }
        }

        internal void RunDetatched(string cmd, string arguments)
        {
            using (Process process = new Process())
            {
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.ErrorDialog = false;
                process.StartInfo.RedirectStandardOutput = false;
                process.StartInfo.RedirectStandardInput = false;

                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.FileName = cmd;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.WorkingDirectory = sccService.CurrentGitWorkingDirectory ??
                    Path.GetDirectoryName(sccService.GetSolutionFileName());
                process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
                process.StartInfo.LoadUserProfile = true;

                process.Start();
            }
        } 
        #endregion
    }
}