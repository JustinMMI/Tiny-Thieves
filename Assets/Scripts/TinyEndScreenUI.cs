using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class TinyEndScreenUI : MonoBehaviour
{
    [Header("Death HUD")]
    [SerializeField] private GameObject deathContextPanel;
    [SerializeField] private Button[] closeDeathContextButtons;
    [SerializeField] private TMP_Text[] alivePlayerTexts = new TMP_Text[3];

    [Header("Lose Screen")]
    [SerializeField] private Button loseMenuButton;
    [SerializeField] private Button loseReplayButton;

    [Header("Win Screen")]
    [SerializeField] private Button winMenuButton;
    [SerializeField] private Button winReplayButton;

    private readonly string[] aliveLines = new string[3];

    private void Awake()
    {
        EnsureCanvasCanReceiveClicks();
        EnsureEventSystem();
        BindButtons();
    }

    private void EnsureCanvasCanReceiveClicks()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = GetComponentInParent<Canvas>();
        }

        if (canvas == null)
        {
            return;
        }

        if (canvas.GetComponent<GraphicRaycaster>() == null)
        {
            canvas.gameObject.AddComponent<GraphicRaycaster>();
        }
    }

    private static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject eventSystemObject = new GameObject("EventSystem");
            eventSystem = eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
            return;
        }

        if (eventSystem.GetComponent<BaseInputModule>() == null)
        {
            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        }
    }

    private void OnEnable()
    {
        RefreshAlivePlayers();
    }

    private void Update()
    {
        RefreshAlivePlayers();
    }

    private void BindButtons()
    {
        if (closeDeathContextButtons != null)
        {
            for (int i = 0; i < closeDeathContextButtons.Length; i++)
            {
                AddListener(closeDeathContextButtons[i], HideDeathContextPanel);
            }
        }

        AddListener(loseMenuButton, TinyNetcodeManager.RequestMainMenuFromEndScreen);
        AddListener(winMenuButton, TinyNetcodeManager.RequestMainMenuFromEndScreen);
        AddListener(loseReplayButton, TinyNetcodeManager.RequestReplayFromEndScreen);
        AddListener(winReplayButton, TinyNetcodeManager.RequestReplayFromEndScreen);
    }

    private void HideDeathContextPanel()
    {
        if (deathContextPanel != null)
        {
            deathContextPanel.SetActive(false);
        }
    }

    private void RefreshAlivePlayers()
    {
        if (alivePlayerTexts == null || alivePlayerTexts.Length == 0)
        {
            return;
        }

        int count = TinyNetcodeManager.GetAlivePlayerHealthLines(aliveLines);
        for (int i = 0; i < alivePlayerTexts.Length; i++)
        {
            if (alivePlayerTexts[i] == null)
            {
                continue;
            }

            bool hasLine = i < count;
            alivePlayerTexts[i].gameObject.SetActive(hasLine);
            alivePlayerTexts[i].text = hasLine ? aliveLines[i] : string.Empty;
        }
    }

    private static void AddListener(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button == null || action == null)
        {
            return;
        }

        button.onClick.RemoveListener(action);
        button.onClick.AddListener(action);
    }
}
