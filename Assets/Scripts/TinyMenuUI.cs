using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public sealed class TinyMenuUI : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject creditsPanel;

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

    [Header("Flow")]
    [SerializeField] private string gameplaySceneName = "SampleScene";
    [SerializeField] private GameObject gameplayRoot;
    [SerializeField] private bool hideGameplayUntilStarted = true;

    private bool gameStarted;
    private bool waitingForJoin;

    private void Awake()
    {
        BindButtons();
        ShowMainMenu();
    }

    private void Update()
    {
        RefreshLobby();
        RefreshJoinFlow();
    }

    public void ShowMainMenu()
    {
        gameStarted = false;
        SetPanel(mainMenuPanel, true);
        SetPanel(lobbyPanel, false);
        SetPanel(settingsPanel, false);
        SetPanel(creditsPanel, false);
        SetGameplayVisible(!hideGameplayUntilStarted);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
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
        SetPanel(creditsPanel, false);
        SetGameplayVisible(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        RefreshLobby();
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
        SetPanel(creditsPanel, false);
        if (string.IsNullOrWhiteSpace(gameplaySceneName))
        {
            SetGameplayVisible(true);
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public void ShowSettings()
    {
        SetPanel(settingsPanel, true);
        SetPanel(creditsPanel, false);
    }

    public void ShowCredits()
    {
        SetPanel(settingsPanel, false);
        SetPanel(creditsPanel, true);
    }

    public void CopyLobbyCode()
    {
        GUIUtility.systemCopyBuffer = TinyNetcodeManager.CurrentRelayJoinCode;
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
        AddListener(playButton, HostGame);
        AddListener(joinButton, JoinGame);
        AddListener(settingsButton, ShowSettings);
        AddListener(creditsButton, ShowCredits);
        AddListener(copyCodeButton, CopyLobbyCode);
        AddListener(leaveLobbyButton, LeaveLobby);
        AddListener(startGameButton, StartGame);
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
            SetText(playerNameTexts, i, occupied ? "Joueur " + (i + 1) + (i == 0 ? "  Hote" : string.Empty) : "En attente...");
            SetText(playerRoleTexts, i, occupied ? (i == 0 ? "Hote de la partie" : "Joueur") : string.Empty);
            SetText(playerReadyTexts, i, occupied ? "PRET" : "?");
        }

        if (startGameButton != null)
        {
            startGameButton.interactable = TinyNetcodeManager.IsHostActive && TinyNetcodeManager.IsClientConnected && !gameStarted;
        }
    }

    private void SetGameplayVisible(bool visible)
    {
        if (gameplayRoot != null)
        {
            gameplayRoot.SetActive(visible);
        }
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

    private static void SetText(TMP_Text[] texts, int index, string value)
    {
        if (texts != null && index >= 0 && index < texts.Length && texts[index] != null)
        {
            texts[index].text = value;
        }
    }
}
