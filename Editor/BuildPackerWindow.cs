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

public class BuildPackerWindow : EditorWindow
{
    private const string errorDialogueTitle = "PackageCreationError";
    private const string settingsSaveKey = "BuildPackerSaveKey";
    private static BuildPackerWindow wnd;
    private static bool packing;
    private static string finalOutputPath = string.Empty;
    private static List<string> loadedMockBackendJurisdictions = new();
    private static string MockBackendFolderPath => Path.GetDirectoryName(settings.mockBackendFilePath);
    private static string lastPackingStep;
    private static ConcurrentBag<string> stepMessages = new();
    private static ConcurrentBag<float> packagingProgressMessages = new();
    private static string ProjectPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static Settings settings;

    private static DropdownField jurisdictionDropdown;
    private static Button mockBackendExePathOpenFileDialogueButton;
    private static Button outputPathOpenFolderDialogueButton;
    private static Button createPackageButton;
    private static TextField mockBackendPathField;
    private static TextField outputPathField;
    private static TextField outputFileNameField;
    private static ProgressBar packingProgressBar;

    private class Settings
    {
        public string outputFileName = string.Empty;
        public string outputFolderPath = string.Empty;
        public string finalOutputPath = string.Empty;
        public string currentJurisdiction = string.Empty;
        public string mockBackendFilePath = string.Empty;
    }

    [MenuItem("Build/Build Packer")]
    public static void ShowExample()
    {
        wnd = GetWindow<BuildPackerWindow>();
        wnd.titleContent = new GUIContent("Build Packer");
    }

    private void Update()
    {
        // Updates "Create Package" button text based on current step. Steps can update from other threads, this
        // allows for Unity's API to be consumed "from" those threads without throwing exceptions.
        if (stepMessages.Count > 0 && stepMessages.TryTake(out string step))
        {
            SetPackagingStep(step);
        }

        if (packagingProgressMessages.Count > 0 && packagingProgressMessages.TryTake(out float progress))
        {
            // Stops the progress bar from reaching the end before the packing process has completed.
            // Depending on the project, it was possible for the progress bar to reach the end
            // due to the things influencing it not reflecting the exact percentage of work they occupy.
            if (packingProgressBar.value < packingProgressBar.highValue * 0.8f)
                packingProgressBar.value += progress;
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

        outputPathField.value = Path.Combine(settings.outputFolderPath, settings.outputFileName);

        if (packing)
        {
            SetButtonStates(false);
            stepMessages.Add(lastPackingStep);
        }

        RegisterToControlEvents(root);
    }

    private void RegisterToControlEvents(VisualElement root)
    {
        outputFileNameField.RegisterValueChangedCallback(OutputFileNameChangeHandler);
        outputPathOpenFolderDialogueButton.clicked += OutputPathOpenFolderDialogueButtonHandler;
        mockBackendExePathOpenFileDialogueButton.clicked += MockBackendExePathOpenFileDialogueButtonHandler;
        jurisdictionDropdown.RegisterValueChangedCallback(JurisdictionDropdownValueChangeHandler);
        root.Q<Button>("CreatePackageButton").clicked += CreatePackageButtonHandler;
    }

    private async Task BeginPackingBuild()
    {
        try
        {
            SetProgressBarDisplay(DisplayStyle.Flex);
            finalOutputPath = Path.GetFullPath(Path.Combine(settings.outputFolderPath, settings.outputFileName));
            packagingProgressMessages.Add(0.1f);

            if (!Directory.Exists(finalOutputPath))
            {
                Directory.CreateDirectory(finalOutputPath);
            }

            packagingProgressMessages.Add(0.1f);
            packing = true;
            SetButtonStates(false);
            await CopyPackageComponents();
            stepMessages.Add("Updating Mock Backend config...");
            UpdateMockBackendConfig();
            packagingProgressMessages.Add(0.3f);

            await Task.Run(() =>
            {
                CompressPackage();
                packagingProgressMessages.Add(0.5f);
                CleanupTempArtifacts();
            });

            packingProgressBar.value = packingProgressBar.highValue;
            EditorUtility.DisplayDialog("Build Packing Complete", "Completed build packing process!", "Ok");
            
            using (Process.Start("explorer.exe", $"/select,\"{finalOutputPath}.zip\""))
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
        stepMessages.Add(string.Empty);
        SetProgressBarDisplay(DisplayStyle.None);
        packingProgressBar.value = 0;
        SetButtonStates(true);
    }

    private void CleanupTempArtifacts()
    {
        stepMessages.Add("Deleting temp files..");
        Directory.Delete(finalOutputPath, true);
    }

    private void CompressPackage()
    {
        stepMessages.Add("Creating zip...");
        using Process zipProc = Process.Start(new ProcessStartInfo()
        {
            FileName = "tar",
            Arguments = $"-a -cf \"{finalOutputPath}.zip\" -C \"{finalOutputPath}\" *",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        zipProc.WaitForExit();
    }

    private async Task CopyPackageComponents()
    {
        string buildFolderPath = Path.Combine(Application.dataPath, "..", "Build");
        stepMessages.Add("Copying build and Mock Backend..");
        await Task.Run(() => CopyDirectory(buildFolderPath, finalOutputPath));
        await Task.Run(() => CopyDirectory(MockBackendFolderPath, finalOutputPath));
        packagingProgressMessages.Add(0.3f);
    }

    private void UpdateMockBackendConfig()
    {
        XmlDocument mockBackendConfig = new XmlDocument();
        mockBackendConfig.Load(Path.Combine(finalOutputPath, Path.GetFileName(MockBackendFolderPath), "MockBackEnd.exe.config"));
        mockBackendConfig.DocumentElement.SelectSingleNode("/configuration/userSettings/Arrow.MockBackend.Properties.Settings/setting[@name='jurisdiction']/value").InnerText = settings.currentJurisdiction;
        mockBackendConfig.Save(Path.Combine(finalOutputPath, Path.GetFileName(MockBackendFolderPath), "MockBackEnd.exe.config"));
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

        outputFileNameField.value = settings.outputFileName;

        if (!string.IsNullOrEmpty(settings.outputFolderPath))
        {
            root.Q<TextField>("OutputPathField").value = Path.GetFullPath($"{settings.outputFolderPath}{settings.outputFileName}");
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
        outputFileNameField = root.Q<TextField>("OutputFileNameField");
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
        outputFileNameField.SetEnabled(status);
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
        if (Directory.Exists(settings.outputFolderPath) && !EditorUtility.DisplayDialog("Output Folder Exists", "The path provided for the output folder already exists! Are you sure you want to override existing pack files?", "Yes", "No"))
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

    private void CopyDirectory(string targetDirectoryPath, string outputDirectoryPath)
    {
        DirectoryInfo newDirectory = Directory.CreateDirectory(Path.Combine(outputDirectoryPath, Path.GetFileName(targetDirectoryPath)));
        stepMessages.Add($"created {Path.GetFileName(targetDirectoryPath)} directory");

        foreach (string file in Directory.EnumerateFiles(targetDirectoryPath))
        {
            FileInfo fi = new FileInfo(file);
            stepMessages.Add($"copying {fi.Name}");
            File.Copy(fi.FullName, Path.Combine(newDirectory.FullName, fi.Name), true);
            packagingProgressMessages.Add(0.001f);
        }

        foreach (string directory in Directory.EnumerateDirectories(targetDirectoryPath))
        {
            string fullDirectoryPath = Path.GetFullPath(directory);

            // Skipping directory if it matches finalOutputPath to avoid infinite recursion. This can happen if the user
            // makes the output path somewhere inside of the build folder.
            if (fullDirectoryPath == finalOutputPath)
                continue;

            CopyDirectory(directory, newDirectory.FullName);
        }
    }

    #region EventHandlers

    private void OutputFileNameChangeHandler(ChangeEvent<string> ev)
    {
        settings.outputFileName = ev.newValue;

        if (!string.IsNullOrEmpty(settings.outputFolderPath))
        {
            outputPathField.value = Path.Combine(settings.outputFolderPath, settings.outputFileName);
        }

        PlayerPrefs.SetString(settingsSaveKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

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
            PlayerPrefs.SetString(settingsSaveKey, JsonUtility.ToJson(settings));
            PlayerPrefs.Save();
        }
    }

    private void OutputPathOpenFolderDialogueButtonHandler()
    {
        string openedPath = EditorUtility.OpenFolderPanel("Output Folder Selection", settings.outputFolderPath, string.Empty);

        if (string.IsNullOrEmpty(openedPath))
        {
            return;
        }

        openedPath = Path.GetFullPath(openedPath);

        if (IsPathAPartOfOtherPath(ProjectPath, openedPath))
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, "The output directory cannot be inside of the project directory! Please choose another path.", "Ok");
            return;
        }

        settings.outputFolderPath = openedPath;
        outputPathField.value = Path.Combine(settings.outputFolderPath, settings.outputFileName);
        PlayerPrefs.SetString(settingsSaveKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    private void JurisdictionDropdownValueChangeHandler(ChangeEvent<string> ev)
    {
        settings.currentJurisdiction = ev.newValue;
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

        if (!OutputFileNameIsValid())
        {
            EditorUtility.DisplayDialog(errorDialogueTitle, "Output File Path must have a value!", "Ok");
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

        if (!OutputFolderPathIsValid(settings.outputFolderPath))
        {
            return false;
        }

        return true;
    }

    private bool OutputFileNameIsValid() => !string.IsNullOrEmpty(settings.outputFileName);

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