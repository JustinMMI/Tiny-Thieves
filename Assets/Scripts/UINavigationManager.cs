using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Manages UI navigation for keyboard and gamepad without requiring a mouse.
/// Tracks whether the player is using a mouse or a keyboard/gamepad and switches
/// the EventSystem selection accordingly so only one highlight is ever visible.
/// Attach to the same GameObject as TinyMenuUI or any persistent GameObject in the scene.
/// Call FocusPanel(panel) whenever a panel becomes active to set the first selected button.
/// </summary>
public sealed class UINavigationManager : MonoBehaviour
{
    private static UINavigationManager instance;

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static UINavigationManager Instance => instance;

    /// <summary>
    /// True while the player last interacted with the mouse.
    /// False while the player last interacted with a keyboard or gamepad.
    /// </summary>
    public bool IsMouseMode { get; private set; } = true;

    // Minimum mouse delta magnitude (in pixels) per frame to count as "mouse moved".
    private const float MouseMoveDeltaThreshold = 2f;

    private EventSystem eventSystem;

    // The panel to restore focus to when switching back to keyboard/gamepad mode.
    private GameObject focusedPanel;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        eventSystem = EventSystem.current;
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }

    private void Update()
    {
        DetectInputModeSwitch();
    }

    /// <summary>
    /// Selects the first interactable Selectable in the given panel.
    /// Only activates the selection visually when in keyboard/gamepad mode.
    /// Call this immediately after activating a panel.
    /// </summary>
    public void FocusPanel(GameObject panel)
    {
        focusedPanel = panel;

        if (IsMouseMode)
        {
            // Remember the panel but don't force a visual selection while on mouse.
            return;
        }

        ApplyFocusToPanel(panel);
    }

    /// <summary>
    /// Explicitly selects a specific Selectable element (button, slider, toggle, etc.).
    /// </summary>
    public void SelectElement(Selectable selectable)
    {
        if (selectable == null || eventSystem == null)
        {
            return;
        }

        eventSystem.SetSelectedGameObject(null);
        selectable.Select();
    }

    /// <summary>
    /// Clears the current selection and forgets the focused panel.
    /// Useful when hiding all panels.
    /// </summary>
    public void ClearSelection()
    {
        focusedPanel = null;

        if (eventSystem != null)
        {
            eventSystem.SetSelectedGameObject(null);
        }
    }

    /// <summary>
    /// Restores focus to the current panel if the EventSystem has lost its selection.
    /// Only acts in keyboard/gamepad mode. Call from TinyMenuUI.Update().
    /// </summary>
    public void RecoverLostFocus()
    {
        if (IsMouseMode)
        {
            return;
        }

        if (focusedPanel == null || !focusedPanel.activeInHierarchy)
        {
            return;
        }

        if (eventSystem != null && eventSystem.currentSelectedGameObject != null)
        {
            return;
        }

        ApplyFocusToPanel(focusedPanel);
    }

    private void DetectInputModeSwitch()
    {
        if (IsMouseActive())
        {
            if (!IsMouseMode)
            {
                SwitchToMouseMode();
            }

            return;
        }

        if (IsKeyboardOrGamepadActive())
        {
            if (IsMouseMode)
            {
                SwitchToKeyboardMode();
            }
        }
    }

    private static bool IsMouseActive()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null)
        {
            return false;
        }

        return mouse.delta.ReadValue().magnitude > MouseMoveDeltaThreshold
            || mouse.leftButton.wasPressedThisFrame
            || mouse.rightButton.wasPressedThisFrame
            || mouse.middleButton.wasPressedThisFrame;
    }

    private static bool IsKeyboardOrGamepadActive()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.upArrowKey.wasPressedThisFrame
                || keyboard.downArrowKey.wasPressedThisFrame
                || keyboard.leftArrowKey.wasPressedThisFrame
                || keyboard.rightArrowKey.wasPressedThisFrame
                || keyboard.tabKey.wasPressedThisFrame
                || keyboard.enterKey.wasPressedThisFrame
                || keyboard.numpadEnterKey.wasPressedThisFrame
                || keyboard.spaceKey.wasPressedThisFrame
                || keyboard.escapeKey.wasPressedThisFrame)
            {
                return true;
            }
        }

        Gamepad gamepad = Gamepad.current;
        if (gamepad != null)
        {
            if (gamepad.leftStick.ReadValue().magnitude > 0.3f
                || gamepad.dpad.ReadValue().magnitude > 0.3f
                || gamepad.buttonSouth.wasPressedThisFrame
                || gamepad.buttonEast.wasPressedThisFrame)
            {
                return true;
            }
        }

        return false;
    }

    private void SwitchToMouseMode()
    {
        IsMouseMode = true;

        if (eventSystem != null)
        {
            eventSystem.SetSelectedGameObject(null);
        }
    }

    private void SwitchToKeyboardMode()
    {
        IsMouseMode = false;
        ApplyFocusToPanel(focusedPanel);
    }

    private void ApplyFocusToPanel(GameObject panel)
    {
        if (panel == null || !panel.activeInHierarchy)
        {
            return;
        }

        Selectable firstSelectable = FindFirstInteractableSelectable(panel);
        SelectElement(firstSelectable);
    }

    private static Selectable FindFirstInteractableSelectable(GameObject panel)
    {
        Selectable[] selectables = panel.GetComponentsInChildren<Selectable>(false);
        List<Selectable> candidates = new List<Selectable>(selectables.Length);

        for (int i = 0; i < selectables.Length; i++)
        {
            Selectable s = selectables[i];
            if (s == null || !s.isActiveAndEnabled || !s.interactable)
            {
                continue;
            }

            // Skip InputFields — they should not be the automatic first focus
            // since they intercept all keyboard input immediately.
            if (s is TMPro.TMP_InputField)
            {
                continue;
            }

            candidates.Add(s);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Pick the top-left-most element based on screen position for a natural reading order.
        candidates.Sort(CompareSelectablesByPosition);
        return candidates[0];
    }

    private static int CompareSelectablesByPosition(Selectable a, Selectable b)
    {
        RectTransform rtA = a.GetComponent<RectTransform>();
        RectTransform rtB = b.GetComponent<RectTransform>();

        if (rtA == null || rtB == null)
        {
            return 0;
        }

        Vector3 posA = rtA.position;
        Vector3 posB = rtB.position;

        // Sort by Y descending (top first), then X ascending (left first).
        const float rowThreshold = 30f;
        if (Mathf.Abs(posA.y - posB.y) > rowThreshold)
        {
            return posB.y.CompareTo(posA.y);
        }

        return posA.x.CompareTo(posB.x);
    }
}
