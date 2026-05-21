using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public sealed class TinyMenuUI : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject partySettingsPanel;
    [SerializeField] private GameObject creditsPanel;
    [SerializeField] private string partySettingsPanelFallbackName = "ParametreDePartiePanel";

    [Header("Main Menu")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button creditsButton;
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private Button joinButton;

    [Header("Lobby")]
    [SerializeField] private TMP_Text lobbyCodeText;
    [SerializeField] private TMP_Text lobbyStatusText;
    [SerializeField] private TMP_Text playersTitleText;
    [SerializeField] private TMP_Text[] playerNameTexts = new TMP_Text[4];
    [SerializeField] private TMP_Text[] playerRoleTexts = new TMP_Text[4];
    [SerializeField] private TMP_Text[] playerReadyTexts = new TMP_Text[4];
    [SerializeField] private Button copyCodeButton;
    [SerializeField] private Button leaveLobbyButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button lobbySettingsButton;

    [Header("Credits")]
    [SerializeField] private Button creditsBackButton;

    [Header("Settings")]
    [SerializeField] private Button settingsBackButton;
    [SerializeField] private Button settingsApplyButton;
    [SerializeField] private Button settingsMainMenuButton;

    [Header("Settings – Sliders")]
    [SerializeField] private Slider settingsSliderMasterVolume;
    [SerializeField] private Slider settingsSliderMusicVolume;
    [SerializeField] private Slider settingsSliderBrightness;
    [SerializeField] private Slider settingsSliderSensitivity;

    [Header("Settings – Lists")]
    [SerializeField] private ScrollRect settingsScrollResolution;
    [SerializeField] private ScrollRect settingsScrollDisplayMode;

    [Header("Settings – Toggles")]
    [SerializeField] private Button settingsButtonVSync;

    [Header("Skin Settings")]
    [SerializeField] private string[] skinNames = { "Vert", "Rouge", "Bleu", "Orange" };
    [SerializeField] private TMP_Text selectedSkinNameText;
    [SerializeField] private TMP_Text skinStateText;
    [SerializeField] private RawImage skinPreviewImage;
    [SerializeField] private bool stretchSkinPreviewImageToWindow = true;
    [SerializeField] private Transform skinPreviewRoot;
    [SerializeField] private Camera skinPreviewCamera;
    [SerializeField] private RenderTexture skinPreviewTexture;
    [SerializeField] private bool skinPreviewTextureMatchesScreen = true;
    [SerializeField] private int skinPreviewTextureAntiAliasing = 4;
    [SerializeField] private GameObject[] skinPreviewPrefabs = new GameObject[4];
    [SerializeField] private Vector3 skinPreviewLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 skinPreviewLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 skinPreviewLocalScale = Vector3.one;
    [SerializeField] private Button previousSkinButton;
    [SerializeField] private Button nextSkinButton;
    [SerializeField] private Button equipSkinButton;
    [SerializeField] private Button closeSettingsButton;

    [Header("Flow")]
    [SerializeField] private string gameplaySceneName = "SampleScene";
    [SerializeField] private GameObject gameplayRoot;
    [SerializeField] private bool hideGameplayUntilStarted = true;

    [Header("Canvas Scaling")]
    [SerializeField] private bool configureCanvasScaler = true;
    [SerializeField] private Vector2 canvasReferenceResolution = new Vector2(1920f, 1080f);
    [SerializeField, Range(0f, 1f)] private float canvasMatchWidthOrHeight = 0.5f;

    private bool gameStarted;
    private bool waitingForJoin;
    private int previewSkinIndex;
    private int activePreviewSkinIndex = -1;
    private GameObject activeSkinPreview;
    private RenderTexture runtimeSkinPreviewTexture;
    private int runtimeSkinPreviewWidth;
    private int runtimeSkinPreviewHeight;

    // Tracks which panel is currently active for focus recovery and Cancel navigation.
    private GameObject activePanel;

    private void Awake()
    {
        ConfigureCanvasScaler();
        ResolveOptionalPanels();
        BindButtons();
        EnsureNavigationManager();
        InitSettingsManager();
        ShowMainMenu();
    }

    private void EnsureNavigationManager()
    {
        if (UINavigationManager.Instance == null)
        {
            gameObject.AddComponent<UINavigationManager>();
        }
    }

    private void InitSettingsManager()
    {
        TinySettingsManager mgr = GetComponent<TinySettingsManager>();
        if (mgr == null)
        {
            mgr = gameObject.AddComponent<TinySettingsManager>();
        }

        mgr.Init(
            settingsSliderMasterVolume,
            settingsSliderMusicVolume,
            settingsSliderBrightness,
            settingsSliderSensitivity,
            settingsScrollResolution,
            settingsScrollDisplayMode,
            settingsButtonVSync);
    }

    private void Update()
    {
        RefreshLobby();
        RefreshJoinFlow();
        RefreshSkinPanel();
        HandleCancelNavigation();
        RecoverLostFocus();
    }

    private void OnDestroy()
    {
        if (runtimeSkinPreviewTexture != null)
        {
            runtimeSkinPreviewTexture.Release();
            Destroy(runtimeSkinPreviewTexture);
        }
    }

    public void ShowMainMenu()
    {
        gameStarted = false;
        SetPanel(mainMenuPanel, true);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, false);
        SetSkinPreviewCameraVisible(false);
        SetGameplayVisible(!hideGameplayUntilStarted);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        activePanel = mainMenuPanel;
        UINavigationManager.Instance?.FocusPanel(mainMenuPanel);
    }

    public void HostGame()
    {
        waitingForJoin = false;
        TinyNetcodeManager.StartHostFromMenu();
        ShowLobby();
    }

    public void JoinGame()
    {
        string code = joinCodeInput != null ? joinCodeInput.text : string.Empty;
        waitingForJoin = TinyNetcodeManager.StartClientFromMenu(code);
    }

    public void ShowLobby()
    {
        SetPanel(mainMenuPanel, false);
        SetPanel(lobbyPanel, true);
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, false);
        SetSkinPreviewCameraVisible(false);
        SetGameplayVisible(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        RefreshLobby();
        activePanel = lobbyPanel;
        UINavigationManager.Instance?.FocusPanel(lobbyPanel);
    }

    public void LeaveLobby()
    {
        TinyNetcodeManager.StopFromMenu();
        waitingForJoin = false;
        ShowMainMenu();
    }

    public void StartGame()
    {
        gameStarted = true;
        TinyNetcodeManager.StartGameFromMenu(gameplaySceneName);
        SetPanel(mainMenuPanel, false);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, false);
        SetSkinPreviewCameraVisible(false);
        if (string.IsNullOrWhiteSpace(gameplaySceneName))
        {
            SetGameplayVisible(true);
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        UINavigationManager.Instance?.ClearSelection();
    }

    public void ShowSettings()
    {
        SetPanel(mainMenuPanel, false);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, true);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, false);
        SetSkinPreviewCameraVisible(false);
        activePanel = settingsPanel;
        UINavigationManager.Instance?.FocusPanel(settingsPanel);
    }

    public void ShowPartySettings()
    {
        ResolveOptionalPanels();
        int equippedSkin = TinyNetcodeManager.LocalEquippedSkinIndex;
        if (equippedSkin >= 0)
        {
            previewSkinIndex = equippedSkin;
        }

        SetPanel(mainMenuPanel, false);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, true);
        SetPanel(creditsPanel, false);
        ConfigureSkinPreviewCamera();
        SetSkinPreviewCameraVisible(true);
        RefreshSkinPanel();
        activePanel = partySettingsPanel;
        UINavigationManager.Instance?.FocusPanel(partySettingsPanel);
    }

    public void ShowCredits()
    {
        SetPanel(mainMenuPanel, false);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, true);
        SetSkinPreviewCameraVisible(false);
        activePanel = creditsPanel;
        UINavigationManager.Instance?.FocusPanel(creditsPanel);
    }

    public void CopyLobbyCode()
    {
        GUIUtility.systemCopyBuffer = TinyNetcodeManager.CurrentRelayJoinCode;
    }

    public void PreviousSkin()
    {
        int count = Mathf.Max(1, TinyNetcodeManager.SkinOptionCount);
        previewSkinIndex = (previewSkinIndex - 1 + count) % count;
        RefreshSkinPanel();
    }

    public void NextSkin()
    {
        int count = Mathf.Max(1, TinyNetcodeManager.SkinOptionCount);
        previewSkinIndex = (previewSkinIndex + 1) % count;
        RefreshSkinPanel();
    }

    public void EquipSkin()
    {
        if (!TinyNetcodeManager.IsSkinTakenByOther(previewSkinIndex))
        {
            TinyNetcodeManager.RequestSkinFromMenu(previewSkinIndex);
        }

        RefreshSkinPanel();
    }

    public void HideSettings()
    {
        SetPanel(settingsPanel, false);
        SetPanel(partySettingsPanel, false);
        SetPanel(creditsPanel, false);
        SetSkinPreviewCameraVisible(false);

        if (!gameStarted && TinyNetcodeManager.IsClientConnected)
        {
            SetPanel(lobbyPanel, true);
            SetPanel(mainMenuPanel, false);
            RefreshLobby();
            activePanel = lobbyPanel;
            UINavigationManager.Instance?.FocusPanel(lobbyPanel);
            return;
        }

        SetPanel(mainMenuPanel, true);
        SetPanel(lobbyPanel, false);
        activePanel = mainMenuPanel;
        UINavigationManager.Instance?.FocusPanel(mainMenuPanel);
    }

    // Allows the player to press Escape/Cancel to navigate back from sub-panels.
    private void HandleCancelNavigation()
    {
        if (gameStarted)
        {
            return;
        }

        bool cancelPressed = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
        if (!cancelPressed)
        {
            return;
        }

        if (activePanel == creditsPanel)
        {
            ShowMainMenu();
        }
        else if (activePanel == settingsPanel)
        {
            HideSettings();
        }
        else if (activePanel == partySettingsPanel)
        {
            ShowLobby();
        }
    }

    // Restores keyboard focus to the active panel when the EventSystem has lost its selection.
    // The manager handles the mouse-mode guard internally.
    private void RecoverLostFocus()
    {
        UINavigationManager.Instance?.RecoverLostFocus();
    }

    private void RefreshJoinFlow()
    {
        if (!waitingForJoin)
        {
            return;
        }

        if (TinyNetcodeManager.IsClientConnected)
        {
            waitingForJoin = false;
            ShowLobby();
            return;
        }

        if (!TinyNetcodeManager.IsConnecting)
        {
            waitingForJoin = false;
        }
    }

    private void BindButtons()
    {
        // Main Menu
        AddListener(playButton, HostGame);
        AddListener(joinButton, JoinGame);
        AddListener(settingsButton, ShowSettings);
        AddListener(creditsButton, ShowCredits);

        // Lobby
        AddListener(copyCodeButton, CopyLobbyCode);
        AddListener(leaveLobbyButton, LeaveLobby);
        AddListener(startGameButton, StartGame);
        AddListener(lobbySettingsButton, ShowPartySettings);

        // Credits
        AddListener(creditsBackButton, ShowMainMenu);

        // Settings
        AddListener(settingsBackButton, HideSettings);
        AddListener(settingsMainMenuButton, ShowMainMenu);
        // settingsApplyButton has no logic yet; listener kept for future implementation.
        AddListener(settingsApplyButton, OnSettingsApply);

        // Skin / Party Settings
        AddListener(previousSkinButton, PreviousSkin);
        AddListener(nextSkinButton, NextSkin);
        AddListener(equipSkinButton, EquipSkin);
        AddListener(closeSettingsButton, HideSettings);
    }

    /// <summary>
    /// Called when the Apply button in the Settings panel is pressed.
    /// Commits all pending settings changes via TinySettingsManager.
    /// </summary>
    public void OnSettingsApply()
    {
        TinySettingsManager.Instance?.ApplyAll();
    }

    private void ResolveOptionalPanels()
    {
        if (settingsPanel == null)
        {
            settingsPanel = FindChildGameObject("SettingsPanel");
        }

        if (partySettingsPanel == null)
        {
            partySettingsPanel = FindChildGameObject(partySettingsPanelFallbackName);
        }

        // Lobby buttons — resolve by GameObject name if not assigned in the Inspector.
        if (leaveLobbyButton == null && lobbyPanel != null)
        {
            leaveLobbyButton = FindButtonInPanel(lobbyPanel, "Quitter");
        }

        if (lobbySettingsButton == null && lobbyPanel != null)
        {
            lobbySettingsButton = FindButtonInPanel(lobbyPanel, "Paramètres");
        }

        // Credits back button — resolve by sprite name pattern if not assigned.
        if (creditsBackButton == null && creditsPanel != null)
        {
            creditsBackButton = FindButtonInPanel(creditsPanel, "Jouer (4)");
        }

        // Settings buttons — resolve by GameObject name if not assigned.
        if (settingsPanel != null)
        {
            if (settingsBackButton == null)
            {
                settingsBackButton = FindButtonInPanel(settingsPanel, "Jouer (1)");
            }

            if (settingsApplyButton == null)
            {
                settingsApplyButton = FindButtonInPanel(settingsPanel, "Jouer (2)");
            }

            if (settingsMainMenuButton == null)
            {
                settingsMainMenuButton = FindButtonInPanel(settingsPanel, "Jouer");
            }

            // Settings sliders — resolve by sibling index if not assigned in the Inspector.
            if (settingsSliderMasterVolume == null)
            {
                settingsSliderMasterVolume = FindSliderInPanel(settingsPanel, "Slider");
            }

            if (settingsSliderMusicVolume == null)
            {
                settingsSliderMusicVolume = FindSliderInPanel(settingsPanel, "Slider (1)");
            }

            if (settingsSliderBrightness == null)
            {
                settingsSliderBrightness = FindSliderInPanel(settingsPanel, "Slider (2)");
            }

            if (settingsSliderSensitivity == null)
            {
                settingsSliderSensitivity = FindSliderInPanel(settingsPanel, "Slider (3)");
            }

            // Settings scroll lists — resolve by name.
            if (settingsScrollResolution == null)
            {
                settingsScrollResolution = FindScrollRectInPanel(settingsPanel, "Scroll View (1)");
            }

            if (settingsScrollDisplayMode == null)
            {
                settingsScrollDisplayMode = FindScrollRectInPanel(settingsPanel, "Scroll View (2)");
            }

            // VSync button — "Jouer (3)" is the first red toggle-style button in the panel.
            if (settingsButtonVSync == null)
            {
                settingsButtonVSync = FindButtonInPanel(settingsPanel, "Jouer (3)");
            }
        }

        ConfigureSkinPreviewCamera();
    }

    private static Button FindButtonInPanel(GameObject panel, string childName)
    {
        if (panel == null || string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        Transform[] children = panel.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && children[i].name == childName)
            {
                Button btn = children[i].GetComponent<Button>();
                if (btn != null)
                {
                    return btn;
                }
            }
        }

        return null;
    }

    private static Slider FindSliderInPanel(GameObject panel, string childName)
    {
        if (panel == null || string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        Transform[] children = panel.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && children[i].name == childName)
            {
                Slider s = children[i].GetComponent<Slider>();
                if (s != null)
                {
                    return s;
                }
            }
        }

        return null;
    }

    private static ScrollRect FindScrollRectInPanel(GameObject panel, string childName)
    {
        if (panel == null || string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        Transform[] children = panel.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && children[i].name == childName)
            {
                ScrollRect sr = children[i].GetComponent<ScrollRect>();
                if (sr != null)
                {
                    return sr;
                }
            }
        }

        return null;
    }

    private void RefreshLobby()
    {
        if (lobbyPanel == null || !lobbyPanel.activeSelf)
        {
            return;
        }

        int playerCount = Mathf.Clamp(TinyNetcodeManager.ConnectedPlayerCount, 0, 4);
        string code = TinyNetcodeManager.CurrentRelayJoinCode;

        if (lobbyCodeText != null)
        {
            lobbyCodeText.text = string.IsNullOrWhiteSpace(code) ? "..." : code;
        }

        if (lobbyStatusText != null)
        {
            lobbyStatusText.text = TinyNetcodeManager.IsClientConnected
                ? "En attente des joueurs..."
                : "Connexion...";
        }

        if (playersTitleText != null)
        {
            playersTitleText.text = "JOUEURS (" + playerCount + "/4)";
        }

        for (int i = 0; i < playerNameTexts.Length; i++)
        {
            bool occupied = i < playerCount;
            int skinIndex = TinyNetcodeManager.GetLobbySkinBySlot(i);
            string skinLabel = skinIndex >= 0 ? " - " + GetSkinName(skinIndex) : string.Empty;
            SetText(playerNameTexts, i, occupied ? "Joueur " + (i + 1) + (i == 0 ? "  Hote" : string.Empty) + skinLabel : "En attente...");
            SetText(playerRoleTexts, i, occupied ? (i == 0 ? "Hote de la partie" : "Joueur") : string.Empty);
            SetText(playerReadyTexts, i, occupied ? "PRET" : "?");
        }

        if (startGameButton != null)
        {
            startGameButton.interactable = TinyNetcodeManager.IsHostActive && TinyNetcodeManager.IsClientConnected && !gameStarted;
        }
    }

    private void RefreshSkinPanel()
    {
        if (partySettingsPanel == null || !partySettingsPanel.activeSelf)
        {
            return;
        }

        int count = Mathf.Max(1, TinyNetcodeManager.SkinOptionCount);
        previewSkinIndex = Mathf.Clamp(previewSkinIndex, 0, count - 1);
        int equippedSkin = TinyNetcodeManager.LocalEquippedSkinIndex;
        bool takenByOther = TinyNetcodeManager.IsSkinTakenByOther(previewSkinIndex);
        bool isEquipped = equippedSkin == previewSkinIndex;

        if (selectedSkinNameText != null)
        {
            selectedSkinNameText.text = GetSkinName(previewSkinIndex);
        }

        if (skinStateText != null)
        {
            skinStateText.text = isEquipped ? "Skin equipe" : (takenByOther ? "Deja pris" : "Disponible");
        }

        if (equipSkinButton != null)
        {
            equipSkinButton.interactable = TinyNetcodeManager.IsClientConnected && !takenByOther && !isEquipped;
        }

        RefreshSkinPreview();
    }

    private void RefreshSkinPreview()
    {
        if (skinPreviewRoot == null || activePreviewSkinIndex == previewSkinIndex)
        {
            return;
        }

        if (activeSkinPreview != null)
        {
            Destroy(activeSkinPreview);
            activeSkinPreview = null;
        }

        GameObject prefab = GetSkinPreviewPrefab(previewSkinIndex);
        if (prefab == null)
        {
            activePreviewSkinIndex = previewSkinIndex;
            return;
        }

        activeSkinPreview = Instantiate(prefab, skinPreviewRoot);
        activeSkinPreview.transform.localPosition = skinPreviewLocalPosition;
        activeSkinPreview.transform.localRotation = Quaternion.Euler(skinPreviewLocalEulerAngles);
        activeSkinPreview.transform.localScale = skinPreviewLocalScale == Vector3.zero ? Vector3.one : skinPreviewLocalScale;
        DisablePreviewRuntimeComponents(activeSkinPreview);
        ConfigureSkinPreviewCamera();
        activePreviewSkinIndex = previewSkinIndex;
    }

    private void ConfigureSkinPreviewCamera()
    {
        if (skinPreviewCamera == null || skinPreviewImage == null)
        {
            return;
        }

        RenderTexture targetTexture = skinPreviewTextureMatchesScreen ? null : skinPreviewTexture;
        if (targetTexture == null)
        {
            EnsureRuntimeSkinPreviewTexture();
            targetTexture = runtimeSkinPreviewTexture;
        }

        skinPreviewCamera.targetTexture = targetTexture;
        skinPreviewImage.texture = targetTexture;
        if (stretchSkinPreviewImageToWindow)
        {
            StretchToWindow(skinPreviewImage.rectTransform);
        }
    }

    private static void StretchToWindow(RectTransform rectTransform)
    {
        if (rectTransform == null)
        {
            return;
        }

        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.localScale = Vector3.one;
    }

    private void SetSkinPreviewCameraVisible(bool visible)
    {
        if (skinPreviewCamera != null)
        {
            skinPreviewCamera.enabled = visible;
        }
    }

    private void EnsureRuntimeSkinPreviewTexture()
    {
        Vector2Int textureSize = GetSkinPreviewTextureSize();
        int width = textureSize.x;
        int height = textureSize.y;
        if (runtimeSkinPreviewTexture != null
            && runtimeSkinPreviewWidth == width
            && runtimeSkinPreviewHeight == height)
        {
            return;
        }

        if (runtimeSkinPreviewTexture != null)
        {
            runtimeSkinPreviewTexture.Release();
            Destroy(runtimeSkinPreviewTexture);
        }

        runtimeSkinPreviewWidth = width;
        runtimeSkinPreviewHeight = height;
        runtimeSkinPreviewTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        runtimeSkinPreviewTexture.name = "Tiny Skin Preview Texture";
        runtimeSkinPreviewTexture.antiAliasing = Mathf.Clamp(skinPreviewTextureAntiAliasing, 1, 8);
        runtimeSkinPreviewTexture.filterMode = FilterMode.Bilinear;
        runtimeSkinPreviewTexture.Create();
    }

    private Vector2Int GetSkinPreviewTextureSize()
    {
        if (!skinPreviewTextureMatchesScreen || skinPreviewImage == null)
        {
            return new Vector2Int(1024, 1024);
        }

        RectTransform previewRect = skinPreviewImage.rectTransform;
        Canvas canvas = skinPreviewImage.canvas;
        float scaleFactor = canvas != null ? Mathf.Max(1f, canvas.scaleFactor) : 1f;
        int width = Mathf.CeilToInt(Mathf.Abs(previewRect.rect.width) * scaleFactor);
        int height = Mathf.CeilToInt(Mathf.Abs(previewRect.rect.height) * scaleFactor);

        if (width <= 1 || height <= 1)
        {
            width = Screen.width;
            height = Screen.height;
        }

        return new Vector2Int(
            Mathf.Clamp(width, 256, 4096),
            Mathf.Clamp(height, 256, 4096));
    }

    private GameObject GetSkinPreviewPrefab(int index)
    {
        if (skinPreviewPrefabs == null || index < 0 || index >= skinPreviewPrefabs.Length)
        {
            return null;
        }

        return skinPreviewPrefabs[index];
    }

    private static void DisablePreviewRuntimeComponents(GameObject previewObject)
    {
        if (previewObject == null)
        {
            return;
        }

        Camera[] cameras = previewObject.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
        {
            cameras[i].enabled = false;
        }

        AudioListener[] listeners = previewObject.GetComponentsInChildren<AudioListener>(true);
        for (int i = 0; i < listeners.Length; i++)
        {
            listeners[i].enabled = false;
        }

        Animator[] animators = previewObject.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            animators[i].enabled = false;
        }

        Collider[] colliders = previewObject.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }

        CharacterController[] characterControllers = previewObject.GetComponentsInChildren<CharacterController>(true);
        for (int i = 0; i < characterControllers.Length; i++)
        {
            characterControllers[i].enabled = false;
        }

        Rigidbody[] rigidbodies = previewObject.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rigidbodies.Length; i++)
        {
            rigidbodies[i].isKinematic = true;
            rigidbodies[i].detectCollisions = false;
        }

        MonoBehaviour[] behaviours = previewObject.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            behaviours[i].enabled = false;
        }
    }

    private string GetSkinName(int index)
    {
        if (skinNames != null && index >= 0 && index < skinNames.Length && !string.IsNullOrWhiteSpace(skinNames[index]))
        {
            return skinNames[index];
        }

        return "Skin " + (index + 1);
    }

    private void SetGameplayVisible(bool visible)
    {
        if (gameplayRoot != null)
        {
            gameplayRoot.SetActive(visible);
        }
    }

    private void ConfigureCanvasScaler()
    {
        if (!configureCanvasScaler)
        {
            return;
        }

        CanvasScaler scaler = GetComponent<CanvasScaler>();
        if (scaler == null)
        {
            scaler = GetComponentInParent<CanvasScaler>();
        }

        if (scaler == null)
        {
            return;
        }

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = canvasReferenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = canvasMatchWidthOrHeight;
    }

    private static void SetPanel(GameObject panel, bool visible)
    {
        if (panel != null)
        {
            panel.SetActive(visible);
        }
    }

    private static void AddListener(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button != null)
        {
            button.onClick.AddListener(action);
        }
    }

    private GameObject FindChildGameObject(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        Transform[] children = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && children[i].name == objectName)
            {
                return children[i].gameObject;
            }
        }

        return null;
    }

    private static void SetText(TMP_Text[] texts, int index, string value)
    {
        if (texts != null && index >= 0 && index < texts.Length && texts[index] != null)
        {
            texts[index].text = value;
        }
    }
}
