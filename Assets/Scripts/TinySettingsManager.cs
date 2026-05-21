using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages all game settings: resolution, display mode, VSync, mouse sensitivity, and brightness.
/// Persists values via PlayerPrefs. Attach this to the same GameObject as TinyMenuUI.
/// Call ApplyAll() to commit pending changes (bound to the Apply button).
/// </summary>
public sealed class TinySettingsManager : MonoBehaviour
{
    // ─── PlayerPrefs keys ────────────────────────────────────────────────────
    private const string KeyResolutionIndex  = "Settings_ResolutionIndex";
    private const string KeyDisplayMode      = "Settings_DisplayMode";
    private const string KeyVSync            = "Settings_VSync";
    private const string KeyMouseSensitivity = "Settings_MouseSensitivity";
    private const string KeyBrightness       = "Settings_Brightness";
    private const string KeyMasterVolume     = "Settings_MasterVolume";
    private const string KeyMusicVolume      = "Settings_MusicVolume";

    // ─── Default values ───────────────────────────────────────────────────────
    private const float DefaultMouseSensitivity = 0.08f;
    private const float DefaultBrightness       = 0f;     // postExposure EV100
    private const float DefaultMasterVolume     = 1f;
    private const float DefaultMusicVolume      = 1f;
    private const int   DefaultDisplayMode      = 0;       // 0 = Windowed
    private const bool  DefaultVSync            = false;

    // ─── Slider range for postExposure (EV100) ────────────────────────────────
    private const float BrightnessMin = -2f;
    private const float BrightnessMax =  2f;

    // ─── Slider range for mouse sensitivity ──────────────────────────────────
    private const float SensitivityMin = 0.01f;
    private const float SensitivityMax = 0.30f;

    // ─── UI References (assigned by TinyMenuUI via Init()) ───────────────────
    private Slider sliderMasterVolume;
    private Slider sliderMusicVolume;
    private Slider sliderBrightness;
    private Slider sliderSensitivity;
    private ScrollRect scrollResolution;
    private ScrollRect scrollDisplayMode;
    private Button    buttonVSync;

    // ─── Volume profile for brightness ───────────────────────────────────────
    [SerializeField] private VolumeProfile sampleSceneProfile;

    // ─── Prefab used to populate scroll lists ─────────────────────────────────
    [SerializeField] private GameObject listItemPrefab;

    // ─── Runtime state ───────────────────────────────────────────────────────
    private Resolution[]     supportedResolutions;
    private int              pendingResolutionIndex;
    private FullScreenMode   pendingDisplayMode;
    private bool             pendingVSync;
    private float            pendingMouseSensitivity;
    private float            pendingBrightness;
    private float            pendingMasterVolume;
    private float            pendingMusicVolume;

    // Tracks VSync toggle visual state (button acts as a toggle)
    private bool vSyncVisualOn;

    // ─── Display mode labels ──────────────────────────────────────────────────
    private static readonly string[] DisplayModeLabels = { "Fenêtré", "Fenêtré plein écran", "Plein écran" };
    private static readonly FullScreenMode[] DisplayModes =
    {
        FullScreenMode.Windowed,
        FullScreenMode.FullScreenWindow,
        FullScreenMode.ExclusiveFullScreen,
    };

    // ─── Singleton ────────────────────────────────────────────────────────────
    private static TinySettingsManager instance;
    public static TinySettingsManager Instance => instance;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this);
            return;
        }
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called by TinyMenuUI.Awake() after all references are resolved.
    /// Assigns UI references and populates the settings panel.
    /// </summary>
    public void Init(
        Slider masterVolume,
        Slider musicVolume,
        Slider brightness,
        Slider sensitivity,
        ScrollRect resolutionScroll,
        ScrollRect displayModeScroll,
        Button vSyncButton)
    {
        sliderMasterVolume = masterVolume;
        sliderMusicVolume  = musicVolume;
        sliderBrightness   = brightness;
        sliderSensitivity  = sensitivity;
        scrollResolution   = resolutionScroll;
        scrollDisplayMode  = displayModeScroll;
        buttonVSync        = vSyncButton;

        LoadFromPrefs();
        SafeBuild(BuildResolutionList,   "BuildResolutionList");
        SafeBuild(BuildDisplayModeList,  "BuildDisplayModeList");
        RefreshVSyncButton();
        BindSliders();
        BindVSyncButton();
        ApplyAllImmediate();
    }

    /// <summary>
    /// Applies and persists all pending settings. Bind to the Apply button.
    /// </summary>
    public void ApplyAll()
    {
        ApplyAllImmediate();
        SaveToPrefs();
    }

    private static void SafeBuild(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Debug.LogError("[TinySettingsManager] " + label + " failed: " + e.Message + "\n" + e.StackTrace);
        }
    }

    /// <summary>
    /// Returns the current mouse sensitivity to be read by TinyFirstPersonController.
    /// </summary>
    public static float GetMouseSensitivity()
    {
        return PlayerPrefs.GetFloat(KeyMouseSensitivity, DefaultMouseSensitivity);
    }

    // ─── Persistence ─────────────────────────────────────────────────────────

    private void LoadFromPrefs()
    {
        pendingMasterVolume     = PlayerPrefs.GetFloat(KeyMasterVolume,     DefaultMasterVolume);
        pendingMusicVolume      = PlayerPrefs.GetFloat(KeyMusicVolume,      DefaultMusicVolume);
        pendingMouseSensitivity = PlayerPrefs.GetFloat(KeyMouseSensitivity, DefaultMouseSensitivity);
        pendingBrightness       = PlayerPrefs.GetFloat(KeyBrightness,       DefaultBrightness);
        pendingVSync            = PlayerPrefs.GetInt(KeyVSync, DefaultVSync ? 1 : 0) == 1;
        pendingDisplayMode      = (FullScreenMode)PlayerPrefs.GetInt(KeyDisplayMode, DefaultDisplayMode);

        supportedResolutions = Screen.resolutions;
        int savedIndex = PlayerPrefs.GetInt(KeyResolutionIndex, -1);
        if (savedIndex >= 0 && savedIndex < supportedResolutions.Length)
        {
            pendingResolutionIndex = savedIndex;
        }
        else
        {
            pendingResolutionIndex = FindCurrentResolutionIndex();
        }

        vSyncVisualOn = pendingVSync;

        // Push loaded values to sliders immediately.
        SetSliderSilent(sliderMasterVolume, pendingMasterVolume);
        SetSliderSilent(sliderMusicVolume,  pendingMusicVolume);
        SetSliderSilent(sliderSensitivity,  NormalizeToSlider(pendingMouseSensitivity, SensitivityMin, SensitivityMax));
        SetSliderSilent(sliderBrightness,   NormalizeToSlider(pendingBrightness, BrightnessMin, BrightnessMax));
    }

    private void SaveToPrefs()
    {
        PlayerPrefs.SetFloat(KeyMasterVolume,     pendingMasterVolume);
        PlayerPrefs.SetFloat(KeyMusicVolume,      pendingMusicVolume);
        PlayerPrefs.SetFloat(KeyMouseSensitivity, pendingMouseSensitivity);
        PlayerPrefs.SetFloat(KeyBrightness,       pendingBrightness);
        PlayerPrefs.SetInt(KeyVSync,              pendingVSync ? 1 : 0);
        PlayerPrefs.SetInt(KeyDisplayMode,        (int)pendingDisplayMode);
        PlayerPrefs.SetInt(KeyResolutionIndex,    pendingResolutionIndex);
        PlayerPrefs.Save();
    }

    // ─── Apply ───────────────────────────────────────────────────────────────

    private void ApplyAllImmediate()
    {
        ApplyVolume();
        ApplyResolutionAndDisplayMode();
        ApplyVSync();
        ApplyBrightness();
        // Mouse sensitivity is read directly by TinyFirstPersonController.
    }

    private void ApplyVolume()
    {
        AudioListener.volume = pendingMasterVolume;
    }

    private void ApplyResolutionAndDisplayMode()
    {
        if (supportedResolutions == null || supportedResolutions.Length == 0)
        {
            return;
        }

        Resolution res = supportedResolutions[Mathf.Clamp(pendingResolutionIndex, 0, supportedResolutions.Length - 1)];
        Screen.SetResolution(res.width, res.height, pendingDisplayMode, res.refreshRateRatio);
    }

    private void ApplyVSync()
    {
        QualitySettings.vSyncCount = pendingVSync ? 1 : 0;
    }

    private void ApplyBrightness()
    {
        if (sampleSceneProfile == null)
        {
            return;
        }

        if (sampleSceneProfile.TryGet<ColorAdjustments>(out ColorAdjustments colorAdj))
        {
            colorAdj.postExposure.overrideState = true;
            colorAdj.postExposure.value = pendingBrightness;
        }
    }

    // ─── Slider binding ──────────────────────────────────────────────────────

    private void BindSliders()
    {
        BindSlider(sliderMasterVolume, OnMasterVolumeChanged);
        BindSlider(sliderMusicVolume,  OnMusicVolumeChanged);
        BindSlider(sliderSensitivity,  OnSensitivityChanged);
        BindSlider(sliderBrightness,   OnBrightnessChanged);
    }

    private static void BindSlider(Slider slider, UnityEngine.Events.UnityAction<float> callback)
    {
        if (slider == null)
        {
            return;
        }

        slider.onValueChanged.AddListener(callback);
    }

    private void OnMasterVolumeChanged(float value)
    {
        pendingMasterVolume = value;
        AudioListener.volume = value;
    }

    private void OnMusicVolumeChanged(float value)
    {
        pendingMusicVolume = value;
        // Future: drive an AudioMixer group when one is added to the project.
    }

    private void OnSensitivityChanged(float normalizedValue)
    {
        pendingMouseSensitivity = Mathf.Lerp(SensitivityMin, SensitivityMax, normalizedValue);
    }

    private void OnBrightnessChanged(float normalizedValue)
    {
        pendingBrightness = Mathf.Lerp(BrightnessMin, BrightnessMax, normalizedValue);
        ApplyBrightness();
    }

    // ─── VSync toggle (button acting as toggle) ───────────────────────────────

    private void BindVSyncButton()
    {
        if (buttonVSync == null)
        {
            return;
        }

        buttonVSync.onClick.AddListener(ToggleVSync);
    }

    private void ToggleVSync()
    {
        vSyncVisualOn = !vSyncVisualOn;
        pendingVSync  = vSyncVisualOn;
        RefreshVSyncButton();
    }

    private void RefreshVSyncButton()
    {
        if (buttonVSync == null)
        {
            return;
        }

        Image img = buttonVSync.GetComponent<Image>();
        if (img != null)
        {
            img.color = vSyncVisualOn
                ? new Color(0.18f, 0.65f, 0.18f, 1f)   // green = ON
                : new Color(0.70f, 0.10f, 0.10f, 1f);  // red   = OFF
        }
    }

    // ─── Scroll lists ─────────────────────────────────────────────────────────

    private void BuildResolutionList()
    {
        if (scrollResolution == null)
        {
            return;
        }

        supportedResolutions = Screen.resolutions;
        Transform content = scrollResolution.content;
        if (content == null)
        {
            Debug.LogWarning("[TinySettingsManager] Scroll View (1) has no Content assigned — resolution list skipped.");
            return;
        }

        ClearChildren(content);

        for (int i = 0; i < supportedResolutions.Length; i++)
        {
            int capturedIndex = i;
            Resolution res = supportedResolutions[i];
            string label = res.width + " × " + res.height + " @ " + Mathf.RoundToInt((float)res.refreshRateRatio.value) + "Hz";
            CreateListItem(content, label, () => OnResolutionSelected(capturedIndex), i == pendingResolutionIndex);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(content as RectTransform);
    }

    private void BuildDisplayModeList()
    {
        if (scrollDisplayMode == null)
        {
            return;
        }

        Transform content = scrollDisplayMode.content;
        if (content == null)
        {
            Debug.LogWarning("[TinySettingsManager] Scroll View (2) has no Content assigned — display mode list skipped.");
            return;
        }

        ClearChildren(content);

        for (int i = 0; i < DisplayModeLabels.Length; i++)
        {
            int capturedIndex = i;
            bool selected = DisplayModes[i] == pendingDisplayMode;
            CreateListItem(content, DisplayModeLabels[i], () => OnDisplayModeSelected(capturedIndex), selected);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(content as RectTransform);
    }

    private void OnResolutionSelected(int index)
    {
        pendingResolutionIndex = index;
        RefreshListSelection(scrollResolution.content, index);
    }

    private void OnDisplayModeSelected(int index)
    {
        pendingDisplayMode = DisplayModes[index];
        RefreshListSelection(scrollDisplayMode.content, index);
    }

    /// <summary>
    /// Creates a text button item inside a ScrollRect content transform.
    /// Falls back to a runtime-generated button if no prefab is assigned.
    /// </summary>
    private void CreateListItem(Transform content, string label, Action onClick, bool selected)
    {
        GameObject item;

        if (listItemPrefab != null)
        {
            item = Instantiate(listItemPrefab, content);
        }
        else
        {
            item = CreateFallbackListItem(content);
        }

        TMP_Text text = item.GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = label;
        }
        else
        {
            // Fallback: try legacy Text
            Text legacyText = item.GetComponentInChildren<Text>();
            if (legacyText != null)
            {
                legacyText.text = label;
            }
        }

        Button btn = item.GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.AddListener(() => onClick());
        }

        MarkListItemSelected(item, selected);
    }

    private static GameObject CreateFallbackListItem(Transform parent)
    {
        // Ensure the content panel has a VerticalLayoutGroup so items stack correctly.
        EnsureVerticalLayout(parent);

        // DefaultControls creates properly-initialized UI GameObjects (with RectTransform).
        GameObject item = new GameObject("ListItem", typeof(RectTransform));
        item.transform.SetParent(parent, false);

        RectTransform rt = item.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 40f);

        Image bg = item.AddComponent<Image>();
        bg.color = new Color(0.2f, 0.2f, 0.2f, 0.85f);

        Button btn = item.AddComponent<Button>();
        btn.targetGraphic = bg;

        // Label — parented to item so it inherits the RectTransform context.
        GameObject textGo = new GameObject("Label", typeof(RectTransform));
        textGo.transform.SetParent(item.transform, false);

        RectTransform textRt = textGo.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(8f, 2f);
        textRt.offsetMax = new Vector2(-8f, -2f);

        TextMeshProUGUI tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.fontSize  = 18f;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.color     = Color.white;

        return item;
    }

    /// <summary>
    /// Adds a VerticalLayoutGroup + ContentSizeFitter to the content transform
    /// so dynamically-created list items stack and resize properly.
    /// Only configures if not already present.
    /// </summary>
    private static void EnsureVerticalLayout(Transform content)
    {
        if (content == null)
        {
            return;
        }

        VerticalLayoutGroup vlg = content.GetComponent<VerticalLayoutGroup>();
        if (vlg == null)
        {
            vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        }

        vlg.childAlignment        = TextAnchor.UpperLeft;
        vlg.childControlWidth     = true;
        vlg.childControlHeight    = false;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing               = 4f;
        vlg.padding               = new RectOffset(4, 4, 4, 4);

        ContentSizeFitter csf = content.GetComponent<ContentSizeFitter>();
        if (csf == null)
        {
            csf = content.gameObject.AddComponent<ContentSizeFitter>();
        }

        csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
    }

    private static void MarkListItemSelected(GameObject item, bool selected)
    {
        Image img = item.GetComponent<Image>();
        if (img != null)
        {
            img.color = selected
                ? new Color(0.18f, 0.65f, 0.18f, 0.9f)
                : new Color(0.2f, 0.2f, 0.2f, 0.85f);
        }
    }

    private static void RefreshListSelection(Transform content, int selectedIndex)
    {
        for (int i = 0; i < content.childCount; i++)
        {
            MarkListItemSelected(content.GetChild(i).gameObject, i == selectedIndex);
        }
    }

    private static void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private int FindCurrentResolutionIndex()
    {
        if (supportedResolutions == null)
        {
            return 0;
        }

        for (int i = 0; i < supportedResolutions.Length; i++)
        {
            Resolution r = supportedResolutions[i];
            if (r.width == Screen.width && r.height == Screen.height)
            {
                return i;
            }
        }

        return supportedResolutions.Length - 1;
    }

    private static float NormalizeToSlider(float value, float min, float max)
    {
        if (Mathf.Approximately(max, min))
        {
            return 0f;
        }

        return Mathf.Clamp01((value - min) / (max - min));
    }

    private static void SetSliderSilent(Slider slider, float normalizedValue)
    {
        if (slider == null)
        {
            return;
        }

        slider.SetValueWithoutNotify(Mathf.Clamp01(normalizedValue));
    }
}
