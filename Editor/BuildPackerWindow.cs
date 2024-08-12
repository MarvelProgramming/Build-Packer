using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Xml;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using Arrow.MaxFusion.GameApi.Core;


public class BuildPackerWindow : EditorWindow
{
    private const string errorDialogueTitle = "Package Creation Error";
    private const string settingsSaveKey = "BuildPackerSaveKey";
    private static BuildPackerWindow wnd;
    private static bool packing;
    private static List<string> loadedMockBackendJurisdictions = new();
    private static string MockBackendFolderPath => Path.GetDirectoryName(settings.mockBackendFilePath);
    private static string lastPackingStep;
    private static string ProjectPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string buildFolderPath;
    private static string productName;
    private static string cachedMockBackendJursidction;
    private static ConcurrentStack<string> excludedJurisdictions = new();
    private static ConcurrentBag<ThreadEvent> threadEvents = new();
    private static object waitObj = new();
    private static bool waitingForMainThreadDialog;
    private static bool mainThreadDialogResponse;
    private static int totalElementsToCompress;
    private static Settings settings;

    private static Button mockBackendExePathOpenFileDialogueButton;
    private static Button outputPathOpenFolderDialogueButton;
    private static Button createPackageButton;
    private static TextField mockBackendPathField;
    private static TextField outputPathField;
    private static DropdownField jurisdictionDropdown;
    private static ProgressBar packingProgressBar;

    #region Helper Classes

    private class Settings
    {
        public string outputZipPath = string.Empty;
        public string currentJurisdiction = string.Empty;
        public string mockBackendFilePath = string.Empty;
    }

    private class ThreadedDialogRequest
    {
        public string Title { get; private set; }
        public string Body { get; private set; }
        public string YesOption { get; private set; }
        public string NoOption { get; private set; }

        public ThreadedDialogRequest(string title, string body, string yesOption, string noOption)
        {
            Title = title;
            Body = body;
            YesOption = yesOption;
            NoOption = noOption;
        }
    }

    private enum ThreadArgType
    {
        Progress,
        StepMessage,
        DialogueRequest
    }

    private class ThreadEvent
    {
        public ThreadArgType ArgType { get; private set; }
        public object Arg { get; private set; }

        public ThreadEvent(ThreadArgType argType, object arg)
        {
            ArgType = argType;
            Arg = arg;
        }
    }

    #endregion

    [MenuItem("Build/Build Packer")]
    public static void ShowExample()
    {
        wnd = GetWindow<BuildPackerWindow>();
        wnd.titleContent = new GUIContent("Build Packer");
    }

    private void Update()
    {
        ProcessThreadEvents();
    }

    private void ProcessThreadEvents()
    {
        if (threadEvents.TryTake(out ThreadEvent threadEvent))
        {
            switch (threadEvent.ArgType)
            {
                case ThreadArgType.Progress:
                    packingProgressBar.value += (float)threadEvent.Arg;
                    break;
                case ThreadArgType.StepMessage:
                    SetPackagingStep((string)threadEvent.Arg);
                    break;
                case ThreadArgType.DialogueRequest:
                    ThreadedDialogRequest req = (ThreadedDialogRequest)threadEvent.Arg;

                    if (string.IsNullOrEmpty(req.NoOption))
                        mainThreadDialogResponse = EditorUtility.DisplayDialog(req.Title, req.Body, req.YesOption);
                    else
                        mainThreadDialogResponse = EditorUtility.DisplayDialog(req.Title, req.Body, req.YesOption, req.NoOption);

                    lock (waitObj)
                    {
                        waitingForMainThreadDialog = false;
                    }

                    break;
                default:
                    throw new Exception($"Invalid ThreadEvent!: {threadEvent.ArgType}");
            }
        }
    }

    public void CreateGUI()
    {
        VisualElement root = rootVisualElement;
        var visualTree = Resources.Load<VisualTreeAsset>("BuildPackerWindow");
        VisualElement labelFromUXML = visualTree.Instantiate();
        root.Add(labelFromUXML);
        InitializeControls(root);
        LoadCachedValues(root);

        buildFolderPath = Path.Combine(Application.dataPath, "..", "Build");
        outputPathField.value = settings.outputZipPath;
        productName = Application.productName;

        if (packing)
        {
            SetButtonStates(false);
            threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, lastPackingStep));
        }

        RegisterToControlEvents(root);
    }

    private void RegisterToControlEvents(VisualElement root)
    {
        outputPathOpenFolderDialogueButton.clicked += OutputPathOpenFileDialogueButtonHandler;
        mockBackendExePathOpenFileDialogueButton.clicked += MockBackendExePathOpenFileDialogueButtonHandler;
        jurisdictionDropdown.RegisterValueChangedCallback(JurisdictionDropdownValueChangeHandler);
        root.Q<Button>("CreatePackageButton").clicked += CreatePackageButtonHandler;
    }

    private async Task BeginPackingBuild()
    {
        try
        {
            SetProgressBarDisplay(DisplayStyle.Flex);
            threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.1f));
            string outputZipTempDirectory = Path.GetFileNameWithoutExtension(settings.outputZipPath);

            if (!Directory.Exists(outputZipTempDirectory))
            {
                if (IsPathAPartOfOtherPath(outputZipTempDirectory, ProjectPath))
                {
                    // Adding "~" to end of output path if it's inside of the project directory
                    // to prevent Unity from importing the packed build, which generally causes issues.
                    outputZipTempDirectory = $"{outputZipTempDirectory}~";
                }

                Directory.CreateDirectory(outputZipTempDirectory);
            }

            threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.1f));
            packing = true;
            SetButtonStates(false);
            await CopyPackageComponents(outputZipTempDirectory);
            threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, "Updating Mock Backend config..."));
            CacheOriginalMockBackendJurisdiction();
            UpdateMockBackendConfig(settings.currentJurisdiction);
            threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.3f));

            await Task.Run(() =>
            {
                CompressPackage(outputZipTempDirectory);
                threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.5f));
            });

            packingProgressBar.value = packingProgressBar.highValue;
            UpdateMockBackendConfig(cachedMockBackendJursidction);
            EditorUtility.DisplayDialog("Build Packing Complete", "Completed build packing process!", "Ok");
            
            using (Process.Start("explorer.exe", $"/select,\"{settings.outputZipPath}\""))
            {
                SetProgressBarDisplay(DisplayStyle.None);
                ResetState();
            }
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, $"An error ocurred while attempting to pack the build! \n\n{ex.Message}", "Ok");
            ResetState();
            throw ex;
        }
    }

    private void ResetState()
    {
        packing = false;
        SetPackagingStep(string.Empty);
        SetProgressBarDisplay(DisplayStyle.None);
        packingProgressBar.value = 0;
        excludedJurisdictions.Clear();
        totalElementsToCompress = 0;
        threadEvents.Clear();
        SetButtonStates(true);
    }

    private void CompressPackage(string outputZipTempDirectory)
    {
        threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, "Creating zip..."));
        string permsToExclude = excludedJurisdictions.Aggregate(string.Empty, (acc, currentJurisdiction) => acc += $"--exclude=\"{currentJurisdiction}\" ").TrimEnd(' ');
        using Process zipProc = Process.Start(new ProcessStartInfo()
        {
            FileName = "tar",
            Arguments = $"-av -cf \"{settings.outputZipPath}\" --exclude={settings.outputZipPath} {permsToExclude} -C {Path.GetFullPath(Path.Combine(buildFolderPath, ".."))} Build -C {Path.GetFullPath(Path.Combine(settings.mockBackendFilePath, "..", ".."))} {Path.GetFileName(Path.GetFullPath(Path.Combine(settings.mockBackendFilePath, "..")))}",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        zipProc.OutputDataReceived += RecievedTarDataHandler;
        zipProc.ErrorDataReceived += RecievedTarDataHandler;
        zipProc.BeginOutputReadLine();
        zipProc.BeginErrorReadLine();
        zipProc.WaitForExit();
    }

    private void RecievedTarDataHandler(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            try
            {
                string relativeFilePath = e.Data.Split(' ')[1];
                threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, $"compressed {Path.GetFileName(relativeFilePath)}"));
                threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.5f / totalElementsToCompress));
            }
            catch (Exception ex)
            {
                threadEvents.Add(new ThreadEvent(ThreadArgType.DialogueRequest, new ThreadedDialogRequest(errorDialogueTitle, $"An error occurred while compressing files!\n\n{ex.Message}", "Ok", string.Empty)));
                ResetState();
                throw ex;
            }
        }
    }

    private async Task CopyPackageComponents(string outputZipTempDirectory)
    {
        threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, "Tallying up files to compress.."));
        Task buildCompressionMapPopulation = Task.Run(() => PopulateCompressionMap(buildFolderPath));
        Task mockBackendCompressionMapPopulation = Task.Run(() => PopulateCompressionMap(MockBackendFolderPath));
        await Task.Run(() => Task.WaitAll(buildCompressionMapPopulation, mockBackendCompressionMapPopulation));
        threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.3f));
    }

    private void CacheOriginalMockBackendJurisdiction()
    {
        XmlDocument mockBackendConfig = new XmlDocument();
        mockBackendConfig.Load(Path.Combine(MockBackendFolderPath, "MockBackEnd.exe.config"));
        cachedMockBackendJursidction = mockBackendConfig.DocumentElement.SelectSingleNode("/configuration/userSettings/Arrow.MockBackend.Properties.Settings/setting[@name='jurisdiction']/value").InnerText;
    }

    private void UpdateMockBackendConfig(string jurisdiction)
    {
        XmlDocument mockBackendConfig = new XmlDocument();
        mockBackendConfig.Load(Path.Combine(MockBackendFolderPath, "MockBackEnd.exe.config"));
        mockBackendConfig.DocumentElement.SelectSingleNode("/configuration/userSettings/Arrow.MockBackend.Properties.Settings/setting[@name='jurisdiction']/value").InnerText = jurisdiction;
        mockBackendConfig.Save(Path.Combine(MockBackendFolderPath, "MockBackEnd.exe.config"));
    }

    private void LoadCachedValues(VisualElement root)
    {
        if (PlayerPrefs.HasKey(settingsSaveKey))
        {
            settings = JsonUtility.FromJson<Settings>(PlayerPrefs.GetString(settingsSaveKey));
        }
        else
        {
            settings = new Settings();
        }

        if (!string.IsNullOrEmpty(settings.outputZipPath))
        {
            root.Q<TextField>("OutputPathField").value = Path.GetFullPath(settings.outputZipPath);
        }

        if (!string.IsNullOrEmpty(settings.mockBackendFilePath))
        {
            mockBackendPathField.value = Path.GetFullPath(settings.mockBackendFilePath);
            PopulateJurisdictionDropdown();
        }

        if (LastJurisdictionHasBeenSaved())
        {
            jurisdictionDropdown.value = settings.currentJurisdiction;
        }
    }

    private void InitializeControls(VisualElement root)
    {
        jurisdictionDropdown = root.Q<DropdownField>("JurisdictionDropdown");
        mockBackendExePathOpenFileDialogueButton = root.Q<Button>("MockBackendExePathOpenFileDialogueButton");
        mockBackendPathField = root.Q<TextField>("MockBackendPathField");
        outputPathField = root.Q<TextField>("OutputPathField");
        outputPathOpenFolderDialogueButton = root.Q<Button>("OutputPathOpenFolderDialogueButton");
        createPackageButton = root.Q<Button>("CreatePackageButton");
        packingProgressBar = root.Q<ProgressBar>("PackingProgressBar");
    }

    private void SetPackagingStep(string step)
    {
        lastPackingStep = step;
        packingProgressBar.title = lastPackingStep;
    }

    private void SetButtonStates(bool status)
    {
        jurisdictionDropdown.SetEnabled(status);
        mockBackendExePathOpenFileDialogueButton.SetEnabled(status);
        mockBackendPathField.SetEnabled(status);
        outputPathField.SetEnabled(status);
        outputPathOpenFolderDialogueButton.SetEnabled(status);
        createPackageButton.SetEnabled(status);
    }

    private void SetProgressBarDisplay(DisplayStyle displayStyle)
    {
        StyleEnum<DisplayStyle> progressBarDisplay = packingProgressBar.style.display;
        progressBarDisplay.value = displayStyle;
        packingProgressBar.style.display = progressBarDisplay;
    }

    private bool LastJurisdictionHasBeenSaved() => loadedMockBackendJurisdictions.Contains(settings.currentJurisdiction);

    private void PopulateJurisdictionDropdown()
    {
        loadedMockBackendJurisdictions = GetMockBackendJurisdictions(MockBackendFolderPath);
        jurisdictionDropdown.choices = loadedMockBackendJurisdictions;
    }

    private List<string> GetMockBackendJurisdictions(string mockBackendFolderPath)
    {
        List<string> mockBackendJurisdictions;

        try
        {
            mockBackendJurisdictions  = new(Directory.EnumerateDirectories(Path.Combine(mockBackendFolderPath, "PermFolder")).Select(jurisdiction => Path.GetFileName(jurisdiction)));
        }
        catch(DirectoryNotFoundException)
        {
            mockBackendJurisdictions = new();
        }

        return mockBackendJurisdictions;
    }

    private bool MockBackendPathIsValid(string mockBackendFolderPath) => File.Exists(Path.Combine(mockBackendFolderPath, "MockBackend.exe"));

    private bool OutputFolderPathIsValid(string path)
    {
        if (File.Exists(settings.outputZipPath) && !EditorUtility.DisplayDialog("Output Folder Exists", "The path provided for the output folder already exists! Are you sure you want to override existing pack files?", "Yes", "No"))
        {
            return false;
        }

        return true;
    }

    private bool BuildExists()
    {
        string buildFolder = Path.Combine(Application.dataPath, "..", "Build");

        if (Directory.Exists(buildFolder) && File.Exists(Path.Combine(buildFolder, $"{Application.productName}.exe")))
        {
            return true;
        }

        return false;
    }

    private async void PopulateCompressionMap(string currentDirectory)
    {
        totalElementsToCompress++;
        threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, $"mapping {Path.GetFileName(currentDirectory)} directory"));

        foreach (string file in Directory.EnumerateFiles(currentDirectory))
        {
            FileInfo fi = new FileInfo(file);

            if (fi.Extension == ".json" && IsPathAPartOfOtherPath(Path.Combine(MockBackendFolderPath, "PermFolder"), fi.FullName) && (!IsPathAPartOfOtherPath(Path.Combine(MockBackendFolderPath, "PermFolder", settings.currentJurisdiction), fi.FullName) || !fi.Name.Contains(productName)))
            {
                excludedJurisdictions.Push(fi.Name);
            }

            threadEvents.Add(new ThreadEvent(ThreadArgType.StepMessage, $"mapping {fi.Name}"));
            totalElementsToCompress++;
            threadEvents.Add(new ThreadEvent(ThreadArgType.Progress, 0.001f));
        }

        foreach (string directory in Directory.EnumerateDirectories(currentDirectory))
        {
            string fullDirectoryPath = Path.GetFullPath(directory);
            DirectoryInfo di = new DirectoryInfo(fullDirectoryPath);
            PopulateCompressionMap(directory);
        }
    }

    private static async Task<bool> CheckIfUserWantsToIncludeCompressedFile(string fileName)
    {
        threadEvents.Add(
                            new ThreadEvent(ThreadArgType.DialogueRequest,
                                new ThreadedDialogRequest("\"Encountered Compressed File\"", $"Do you want to include {fileName} (from your project's build directory) in the final pack?", "Yes", "No")));

        lock (waitObj)
        {
            waitingForMainThreadDialog = true;
        }

        return await Task.Run(() =>
        {
            while (true)
            {
                lock (waitObj)
                {
                    if (!waitingForMainThreadDialog)
                    {
                        break;
                    }
                }

                Thread.Sleep(500);
            }

            return mainThreadDialogResponse;
        });
    }

    #region EventHandlers

    private void MockBackendExePathOpenFileDialogueButtonHandler()
    {
        string openedPath = EditorUtility.OpenFilePanel("Mock Backend Exe Selection", settings.mockBackendFilePath, "exe");

        if (string.IsNullOrEmpty(openedPath))
        {
            return;
        }

        settings.mockBackendFilePath = Path.GetFullPath(openedPath);

        if (!File.Exists(settings.mockBackendFilePath))
        {
            EditorUtility.DisplayDialog("Invalid Mock Backend Path", "The provided path does not contain the \"MockBackEnd.exe\" file!", "Ok");
        }
        else
        {
            mockBackendPathField.value = settings.mockBackendFilePath;
            PopulateJurisdictionDropdown();
            Save();
        }
    }

    private void OutputPathOpenFileDialogueButtonHandler()
    {
        string dialogueStartingDirectory = string.IsNullOrEmpty(settings.outputZipPath) ? $"{Application.productName}_BuildPack" : settings.outputZipPath;
        string openedPath = EditorUtility.SaveFilePanel("Save Packed Zip", string.Empty, dialogueStartingDirectory, "zip");

        if (string.IsNullOrEmpty(openedPath))
        {
            return;
        }

        openedPath = Path.GetFullPath(openedPath);
        settings.outputZipPath = openedPath;
        outputPathField.value = settings.outputZipPath;
        Save();
    }

    private void JurisdictionDropdownValueChangeHandler(ChangeEvent<string> ev)
    {
        settings.currentJurisdiction = ev.newValue;
        Save();
    }

    private static void Save()
    {
        PlayerPrefs.SetString(settingsSaveKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    private async void CreatePackageButtonHandler()
    {
        if (!AllFieldsAreValid())
            return;

        await BeginPackingBuild();
    }

    private bool AllFieldsAreValid()
    {
        if (!BuildExists())
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, "Could not find existing build! Make a build and try again!", "Ok");
            return false;
        }

        if (!MockBackendPathIsValid(MockBackendFolderPath))
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, "Mock Backend path is invalid! The MockBackend.exe file could not be found!", "Ok");
            return false;
        }

        if (loadedMockBackendJurisdictions.Count == 0)
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, "No perms loaded! Add perms to your Mock Backend and try again!", "Ok");
            return false;
        }

        if (!OutputFolderPathIsValid(settings.outputZipPath))
        {
            return false;
        }

        return true;
    }

    private bool IsPathAPartOfOtherPath(string path, string otherPath)
    {
        if (string.IsNullOrEmpty(otherPath) || Path.GetFullPath(Path.Combine(otherPath, "..")) == otherPath)
        {
            return false;
        }

        if (Path.GetFullPath(path) == Path.GetFullPath(otherPath))
        {
            return true;
        }

        return IsPathAPartOfOtherPath(path, Path.GetFullPath(Path.Combine(otherPath, "..")));
    }

    #endregion
}