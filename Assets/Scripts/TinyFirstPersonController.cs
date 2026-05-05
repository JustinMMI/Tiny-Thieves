using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public sealed class TinyFirstPersonController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraPivot;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.08f;
    [SerializeField] private float minPitch = -85f;
    [SerializeField] private float maxPitch = 85f;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 1.65f;
    [SerializeField] private float sprintSpeed = 2.55f;
    [SerializeField] private float crouchSpeed = 0.95f;
    [SerializeField] private float acceleration = 18f;
    [SerializeField] private float jumpHeight = 0.42f;
    [SerializeField] private float gravity = -12f;

    [Header("Tiny Body")]
    [SerializeField] private float standingHeight = 0.55f;
    [SerializeField] private float crouchingHeight = 0.32f;
    [SerializeField] private float standingEyeHeight = 0.43f;
    [SerializeField] private float crouchingEyeHeight = 0.25f;
    [SerializeField] private float crouchLerpSpeed = 14f;

    private CharacterController controller;
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private float pitch;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraPivot == null && Camera.main != null)
        {
            cameraPivot = Camera.main.transform;
        }

        ConfigureTinyBody(standingHeight);
        MoveCameraToEyeHeight(standingEyeHeight, true);
    }

    private void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        Look();
        Move();
        UpdateCrouch();
    }

    private void Look()
    {
        if (Mouse.current == null || cameraPivot == null || Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();
        transform.Rotate(Vector3.up * (mouseDelta.x * mouseSensitivity));

        pitch = Mathf.Clamp(pitch - mouseDelta.y * mouseSensitivity, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void Move()
    {
        Vector2 input = ReadMoveInput();
        Vector3 targetDirection = transform.right * input.x + transform.forward * input.y;
        targetDirection = Vector3.ClampMagnitude(targetDirection, 1f);

        bool isCrouching = IsCrouchHeld();
        float targetSpeed = isCrouching ? crouchSpeed : IsSprintHeld() ? sprintSpeed : walkSpeed;
        Vector3 targetVelocity = targetDirection * targetSpeed;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);

        if (controller.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -1.5f;
        }

        if (controller.isGrounded && !isCrouching && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private void UpdateCrouch()
    {
        bool isCrouching = IsCrouchHeld();
        float targetHeight = isCrouching ? crouchingHeight : standingHeight;
        float targetEyeHeight = isCrouching ? crouchingEyeHeight : standingEyeHeight;

        ConfigureTinyBody(Mathf.Lerp(controller.height, targetHeight, crouchLerpSpeed * Time.deltaTime));
        MoveCameraToEyeHeight(targetEyeHeight, false);
    }

    private Vector2 ReadMoveInput()
    {
        Vector2 input = Vector2.zero;

        if (Keyboard.current.dKey.isPressed)
        {
            input.x += 1f;
        }

        if (Keyboard.current.qKey.isPressed || Keyboard.current.aKey.isPressed)
        {
            input.x -= 1f;
        }

        if (Keyboard.current.zKey.isPressed || Keyboard.current.wKey.isPressed)
        {
            input.y += 1f;
        }

        if (Keyboard.current.sKey.isPressed)
        {
            input.y -= 1f;
        }

        return Vector2.ClampMagnitude(input, 1f);
    }

    private bool IsSprintHeld()
    {
        return Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
    }

    private bool IsCrouchHeld()
    {
        return Keyboard.current.leftCtrlKey.isPressed
            || Keyboard.current.rightCtrlKey.isPressed
            || Keyboard.current.cKey.isPressed;
    }

    private void ConfigureTinyBody(float height)
    {
        controller.height = height;
        controller.radius = 0.16f;
        controller.center = Vector3.up * (height * 0.5f);
        controller.stepOffset = 0.12f;
    }

    private void MoveCameraToEyeHeight(float targetEyeHeight, bool snap)
    {
        if (cameraPivot == null)
        {
            return;
        }

        Vector3 targetPosition = new Vector3(0f, targetEyeHeight, 0f);
        cameraPivot.localPosition = snap
            ? targetPosition
            : Vector3.Lerp(cameraPivot.localPosition, targetPosition, crouchLerpSpeed * Time.deltaTime);
    }
}
