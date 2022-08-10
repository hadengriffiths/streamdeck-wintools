using BarRaider.SdTools;
using BarRaider.SdTools.Wrappers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WindowsInput;
using WinTools.Backend;
using WinTools.Wrappers;

namespace WinTools
{

    //---------------------------------------------------
    //          BarRaider's Hall Of Fame
    // 143 Bits: Rykouh
    // 924 Bits: inclaved
    // Subscriber: SP__LIT
    // Subscriber: Tek_Soup
    // Subscriber: CyberlightGames
    //---------------------------------------------------

    [PluginActionId("com.barraider.wintools.windowsexplorer")]
    public class WindowsExplorerAction : PluginBase
    {

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, ShowWindowEnum nCmdShow);
        
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

        private class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                PluginSettings instance = new PluginSettings
                {
                    Path = String.Empty,
                    PlaySoundOnSet = false,
                    PlaybackDevices = null,
                    PlaybackDevice = String.Empty,
                    PlaySoundOnSetFile = String.Empty,
                    LongKeypressTime = LONG_KEYPRESS_LENGTH_MS.ToString(),
                    LockSelection = false,
                    ShortPressOpenExplorer = true,
                    ShortPressCopyToClipboard = false,
                    Encoding = String.Empty
                };
                return instance;
            }

            [JsonProperty(PropertyName = "path")]
            public String Path { get; set; }

            [JsonProperty(PropertyName = "longKeypressTime")]
            public string LongKeypressTime { get; set; }

            [JsonProperty(PropertyName = "playSoundOnSet")]
            public bool PlaySoundOnSet { get; set; }

            [JsonProperty(PropertyName = "playbackDevices")]
            public List<PlaybackDevice> PlaybackDevices { get; set; }

            [JsonProperty(PropertyName = "playbackDevice")]
            public string PlaybackDevice { get; set; }

            [FilenameProperty]
            [JsonProperty(PropertyName = "playSoundOnSetFile")]
            public string PlaySoundOnSetFile { get; set; }

            [JsonProperty(PropertyName = "lockSelection")]
            public bool LockSelection { get; set; }

            [JsonProperty(PropertyName = "shortPressOpenExplorer")]
            public bool ShortPressOpenExplorer { get; set; }

            [JsonProperty(PropertyName = "shortPressCopyToClipboard")]
            public bool ShortPressCopyToClipboard { get; set; }

            [JsonProperty(PropertyName = "encoding")]
            public string Encoding { get; set; }
        }

        #region Private Members
        private const int LONG_KEYPRESS_LENGTH_MS = 600;

        private bool longKeyPressed = false;
        private int longKeypressTime = LONG_KEYPRESS_LENGTH_MS;
        private readonly System.Timers.Timer tmrRunLongPress = new System.Timers.Timer();
        private string pathTitle;

        private readonly InputSimulator iis = new InputSimulator();
        private readonly PluginSettings settings;
        private GlobalSettings global;
        private TitleParameters titleParameters;

        #endregion
        public WindowsExplorerAction(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                this.settings = PluginSettings.CreateDefaultSettings();
                SaveSettings();
            }
            else
            {
                this.settings = payload.Settings.ToObject<PluginSettings>();
            }
            Connection.OnTitleParametersDidChange += Connection_OnTitleParametersDidChange;
            tmrRunLongPress.Interval = longKeypressTime;
            tmrRunLongPress.Elapsed += TmrRunLongPress_Elapsed;

            InitializeSettings();
            GlobalSettingsManager.Instance.RequestGlobalSettings();
            SetPathTitle();
        }

        public override void Dispose()
        {
            tmrRunLongPress.Stop();
            Connection.OnTitleParametersDidChange -= Connection_OnTitleParametersDidChange;
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Destructor called");
        }

        public override void KeyPressed(KeyPayload payload)
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Key Pressed {this.GetType()}");
            longKeyPressed = false;

            tmrRunLongPress.Interval = longKeypressTime > 0 ? longKeypressTime : LONG_KEYPRESS_LENGTH_MS;
            tmrRunLongPress.Start();
        }

        public override void KeyReleased(KeyPayload payload)
        {
            tmrRunLongPress.Stop();
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Key Released {this.GetType()}");
            if (!longKeyPressed) // Take care of the short keypress
            {
                HandleShortKeyPress();
            }
        }

        public async override void OnTick()
        {
            // Show the folder that is stored in the path
            await Connection.SetTitleAsync(pathTitle);
        }

        public async override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            Tools.AutoPopulateSettings(settings, payload.Settings);
            InitializeSettings();
            await SetGlobalSettings();
            await SaveSettings();
        }

        public async override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) 
        {
            // Global Settings exist
            if (payload?.Settings != null && payload.Settings.Count > 0)
            {
                bool currentPlaySoundOnSet = settings.PlaySoundOnSet;
                global = payload.Settings.ToObject<GlobalSettings>();
                settings.PlaySoundOnSet = global.PlaySoundOnSet;
                settings.PlaybackDevice = global.PlaybackDevice;
                settings.PlaySoundOnSetFile = global.PlaySoundOnSetFile;
                await SaveSettings();

                if (settings.PlaySoundOnSet != currentPlaySoundOnSet)
                {
                    PropagatePlaybackDevices();
                }

            }
            else // Global settings do not exist
            {
                await SetGlobalSettings();
            }
        }

        #region Private Methods
        private void TmrRunLongPress_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            tmrRunLongPress.Stop(); // Should only run once
            _ = HandleLongKeyPress();
        }

        private Task SaveSettings()
        {
            return Connection.SetSettingsAsync(JObject.FromObject(settings));
        }

        private void HandleShortKeyPress()
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Short Keypress");


            if (settings.ShortPressOpenExplorer)
            {

                // Open a windows explorer to the stored path
                LaunchWindowsExplorer();
            }
            else if (settings.ShortPressCopyToClipboard)
            {
                CopyFolderToClipboard();
            }
        }

        private async Task HandleLongKeyPress()
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Long Keypress");
            longKeyPressed = true;

            if (settings.LockSelection)
            {
                iis.Keyboard.TextEntry(settings.Path.Replace("/","\\") + "\\");
                return;

            }

            settings.Path = await GetWindowsExplorerPath();

            if (!String.IsNullOrEmpty(settings.Path))
            {
                await PlaySoundOnSet();
            }

            await SaveSettings();
            SetPathTitle();
        }

        private async Task<string> GetWindowsExplorerPath()
        {
            SHDocVw.ShellWindows shellWindows = new SHDocVw.ShellWindows();

            string filename;
            IntPtr foregroundHwnd = GetForegroundWindow();

            foreach (SHDocVw.InternetExplorer ie in shellWindows)
            {
                if (ie.HWND == foregroundHwnd.ToInt64())
                {
                    try
                    {
                        filename = Path.GetFileNameWithoutExtension(ie.FullName).ToLowerInvariant(); // Verify it's Windows Explorer

                        if (filename.Equals("explorer"))
                        {
                            Logger.Instance.LogMessage(TracingLevel.DEBUG, $"Path: {ie.Path} Name: {ie.Name} Location: {ie.LocationURL}");
                            // Save the location off to your application
                            Uri uri = new Uri(ie.LocationURL);

                            if (String.IsNullOrWhiteSpace(settings.Encoding))
                            {
                                return Uri.UnescapeDataString(uri.LocalPath);
                            }

                            Encoding e = Encoding.GetEncoding(settings.Encoding);
                            return System.Web.HttpUtility.UrlDecode(uri.LocalPath, e);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.LogMessage(TracingLevel.ERROR, $"Failed to GetWindowsExplorerPath {ex}");
                    }
                }
            }
            Logger.Instance.LogMessage(TracingLevel.WARN, "Active window is not a Windows Explorer window");
            await Connection.ShowAlert();
            return null;
        }

        private void SetPathTitle()
        {
            pathTitle = String.Empty;
            try
            {
                if (!String.IsNullOrEmpty(settings.Path))
                {
                    DirectoryInfo di = new DirectoryInfo(settings.Path);
                    pathTitle = Tools.SplitStringToFit(di.Name, titleParameters);                    
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, $"Failed to get Path Title for {settings.Path} {ex}");
            }
        }

        private bool BringExistingExplorerToFront(string explorerPath)
        {
            SHDocVw.ShellWindows shellWindows = new SHDocVw.ShellWindows();

            string filename;
            foreach (SHDocVw.InternetExplorer ie in shellWindows)
            {
                try
                {
                    filename = Path.GetFileNameWithoutExtension(ie.FullName).ToLowerInvariant(); // Verify it's Windows Explorer

                    if (filename.Equals("explorer"))
                    {
                        // Save the location off to your application
                        Uri uri = new Uri(ie.LocationURL);
                        string currentPath = Uri.UnescapeDataString(uri.LocalPath);
                        if (currentPath.ToLowerInvariant() == explorerPath.ToLowerInvariant())
                        {
                            IntPtr destinationProcess = new IntPtr(ie.HWND);
                            if (BringWindowToForeground(destinationProcess))
                            {
                                Logger.Instance.LogMessage(TracingLevel.INFO, $"Successfully set foreground window for Explorer with path {currentPath} HWND: {ie.HWND}");
                                return true;
                            }
                            Logger.Instance.LogMessage(TracingLevel.WARN, $"Failed to set foreground window for Explorer with path {currentPath} HWND: {ie.HWND}, trying to force it");
                            MinimizeAndRestoreWindow(destinationProcess);
                            //Logger.Instance.LogMessage(TracingLevel.ERROR, $"Failed to set foreground window for Explorer with path {currentPath} HWND: {ie.HWND}");
                        }

                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.ERROR, $"Failed to GetWindowsExplorerPath {ex}");
                }
            }
            return false;
        }

        private void CopyFolderToClipboard()
        {
            if (String.IsNullOrEmpty(settings.Path))
            {
                Logger.Instance.LogMessage(TracingLevel.WARN, $"CopyFolderToClipboard called, but Path is empty");
                return;
            }

            SetClipboard(settings.Path);
        }

        private void LaunchWindowsExplorer()
        {
            if (String.IsNullOrEmpty(settings.Path))
            {
                Logger.Instance.LogMessage(TracingLevel.WARN, $"LaunchWindowsExplorer called, but Path is empty");
                return;
            }

            try
            {
                if (BringExistingExplorerToFront(settings.Path))
                {
                    Logger.Instance.LogMessage(TracingLevel.INFO, $"LaunchWindowsExplorer used existing window");
                    return;
                }
                Logger.Instance.LogMessage(TracingLevel.INFO, $"LaunchWindowsExplorer launching new instance");
                // Prepare the process to run
                ProcessStartInfo start = new ProcessStartInfo
                {

                    // Enter the executable to run, including the complete path
                    FileName = settings.Path.Replace('/', '\\'),
                    WindowStyle = ProcessWindowStyle.Normal
                };

                // Launch the app
                var p = Process.Start(start);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, $"LaunchWindowsExplorer exception for path: {settings.Path} {ex}");
                Connection.ShowAlert();
            }
        }

        private void PropagatePlaybackDevices()
        {
            settings.PlaybackDevices = new List<PlaybackDevice>();

            try
            {
                if (settings.PlaySoundOnSet)
                {
                    settings.PlaybackDevices = AudioUtils.Common.GetAllPlaybackDevices(true).Select(d => new PlaybackDevice() { ProductName = d }).ToList();
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, $"Error propagating playback devices {ex}");
            }
        }

        private void InitializeSettings()
        {
            if (!Int32.TryParse(settings.LongKeypressTime, out longKeypressTime))
            {
                settings.LongKeypressTime = LONG_KEYPRESS_LENGTH_MS.ToString();
                SaveSettings();
            }

            // Backwards compatibility 
            if (!settings.ShortPressOpenExplorer && !settings.ShortPressCopyToClipboard)
            {
                settings.ShortPressOpenExplorer = true;
                SaveSettings();
            }

            PropagatePlaybackDevices();
        }

        private async Task PlaySoundOnSet()
        {
            
                if (!settings.PlaySoundOnSet)
                {
                    return;
                }

                if (String.IsNullOrEmpty(settings.PlaySoundOnSetFile) || string.IsNullOrEmpty(settings.PlaybackDevice))
                {
                    Logger.Instance.LogMessage(TracingLevel.WARN, $"PlaySoundOnSet called but File or Playback device are empty. File: {settings.PlaySoundOnSetFile} Device: {settings.PlaybackDevice}");
                    return;
                }

                if (!File.Exists(settings.PlaySoundOnSetFile))
                {
                    Logger.Instance.LogMessage(TracingLevel.WARN, $"PlaySoundOnSet called but file does not exist: {settings.PlaySoundOnSetFile}");
                    return;
                }

                Logger.Instance.LogMessage(TracingLevel.INFO, $"PlaySoundOnEnd called. Playing {settings.PlaySoundOnSetFile} on device: {settings.PlaybackDevice}");

                await AudioUtils.Common.PlaySound(settings.PlaySoundOnSetFile, settings.PlaybackDevice);
        }
        
        private bool BringWindowToForeground(IntPtr hWnd)
        {
            WindowPlacement placement = new WindowPlacement();
            placement.Length = Marshal.SizeOf(placement);
            if (!GetWindowPlacement(hWnd, ref placement))
            {
                return false;
            }
            if (placement.ShowCmd == ShowWindowEnum.SHOWMINIMIZED && !ShowWindow(hWnd, ShowWindowEnum.RESTORE))
            {
                return false;
            }
            return SetForegroundWindow(hWnd);
        }

        private void MinimizeAndRestoreWindow(IntPtr hWnd)
        {
            ShowWindow(hWnd, ShowWindowEnum.MINIMIZE);
            ShowWindow(hWnd, ShowWindowEnum.SHOWNORMAL);
        }

        private async Task SetGlobalSettings()
        {
            if (global == null)
            {
                Logger.Instance.LogMessage(TracingLevel.WARN, "SetGlobalSettings called while Global Settings are null, creating new object");
                global = new GlobalSettings();
            }

            global.PlaySoundOnSet = settings.PlaySoundOnSet;
            global.PlaybackDevice = settings.PlaybackDevice;
            global.PlaySoundOnSetFile = settings.PlaySoundOnSetFile;
            await Connection.SetGlobalSettingsAsync(JObject.FromObject(global));
        }

        private void Connection_OnTitleParametersDidChange(object sender, SDEventReceivedEventArgs<BarRaider.SdTools.Events.TitleParametersDidChange> e)
        {
            titleParameters = e.Event?.Payload?.TitleParameters;
            SetPathTitle();
        }
        private void SetClipboard(string text)
        {
            Thread staThread = new Thread(
                delegate ()
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }

                    catch (Exception ex)
                    {
                        Logger.Instance.LogMessage(TracingLevel.ERROR, $"SetClipboard exception: {ex}");
                    }
                });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
        }


        #endregion
    }
}
