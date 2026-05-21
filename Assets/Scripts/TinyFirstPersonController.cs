using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public sealed class TinyFirstPersonController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private TinyRaymanBody raymanBody;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.08f;
    [SerializeField] private float minPitch = -85f;
    [SerializeField] private float maxPitch = 85f;

    /// <summary>
    /// Returns the effective mouse sensitivity: from TinySettingsManager if available,
    /// otherwise falls back to the serialized Inspector value.
    /// </summary>
    private float EffectiveSensitivity => TinySettingsManager.Instance != null
        ? TinySettingsManager.GetMouseSensitivity()
        : mouseSensitivity;

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
    [SerializeField] private float standingEyeHeight = 0.46f;
    [SerializeField] private float crouchingEyeHeight = 0.25f;
    [SerializeField] private float cameraForwardOffset = 0.2f;
    [SerializeField] private Vector3 manualCameraLocalOffset = Vector3.zero;
    [SerializeField] private Vector3 manualHitboxCenterOffset = Vector3.zero;
    [SerializeField] private float crouchLerpSpeed = 14f;

    [Header("Camera Bob")]
    [SerializeField] private float walkBobSpeed = 14f;
    [SerializeField] private float sprintBobSpeed = 19f;
    [SerializeField] private float crouchBobSpeed = 7f;
    [SerializeField] private float walkBobAmount = 0.018f;
    [SerializeField] private float sprintBobAmount = 0.025f;
    [SerializeField] private float crouchBobAmount = 0.009f;
    [SerializeField] private float bobReturnSpeed = 18f;

    [Header("Climbing")]
    [SerializeField] private float climbDuration = 0.68f;
    [SerializeField] private float climbLiftHeight = 0.12f;
    [SerializeField] private float climbCameraPull = 0.035f;
    [SerializeField] private float climbLookInfluence = 0.65f;
    [SerializeField, Range(0.1f, 1f)] private float oneHandItemClimbSpeedMultiplier = 0.6f;
    [SerializeField, Range(90f, 170f)] private float maxClimbFacingAngle = 115f;
    [SerializeField] private LayerMask landingClearanceMask = ~0;

    [Header("Items")]
    [SerializeField] private float itemLookDistance = 0.85f;
    [SerializeField] private LayerMask itemInteractionMask = ~0;
    [SerializeField] private Vector3 heldItemLocalPosition = new Vector3(0f, -0.08f, 0.48f);
    [SerializeField] private float heldItemBobAmount = 0.018f;
    [SerializeField] private float heldItemIdleBobAmount = 0.006f;
    [SerializeField] private float heldItemIdleBobSpeed = 3f;
    [SerializeField] private float heldItemMotionSharpness = 14f;
    [SerializeField] private float itemGrabHandDuration = 0.12f;
    [SerializeField] private float itemGrabPullDuration = 0.18f;
    [SerializeField] private float itemThrowForce = 4.5f;
    [SerializeField] private float itemThrowWeightSlowdown = 0.12f;
    [SerializeField, Range(0.15f, 1f)] private float minimumCarrySpeedMultiplier = 0.35f;
    [SerializeField, Min(0f)] private float carryWeightSlowdown = 0.08f;
    [SerializeField] private Color itemHighlightColor = new Color(1f, 0.92f, 0.18f, 1f);

    [Header("Rail Wagons")]
    [SerializeField] private float wagonLookDistance = 1.15f;
    [SerializeField] private LayerMask wagonInteractionMask = ~0;
    [SerializeField] private float wagonPlayerFollowSharpness = 12f;

    [Header("Health")]
    [SerializeField] private float maxHealth = 100f;
    [SerializeField] private float regenDelay = 15f;
    [SerializeField] private float regenPerSecond = 8f;
    [SerializeField] private Texture2D[] damageScreenStages = new Texture2D[5];
    [SerializeField] private Sprite[] damageScreenStageSprites = new Sprite[5];
    [SerializeField] private Color deadScreenTint = new Color(0.28f, 0.28f, 0.28f, 0.38f);
    [SerializeField] private Vector3 spectateCameraOffset = new Vector3(0f, 0.85f, -1.8f);
    [SerializeField] private float spectateFollowSharpness = 8f;
    [SerializeField] private bool debugLogHealth = true;

    [Header("Debug")]
    [SerializeField] private bool showClimbZonesInGame;
    [SerializeField] private Color climbZoneDebugColor = new Color(0.1f, 0.65f, 1f, 0.95f);
    [SerializeField] private Color landingZoneDebugColor = new Color(0.2f, 1f, 0.45f, 0.95f);

    private static Material debugLineMaterial;
    private CharacterController controller;
    private readonly List<ClimbZone> climbZones = new List<ClimbZone>();
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private float currentEyeHeight;
    private float bobTimer;
    private float bobOffset;
    private float pitch;
    private bool isClimbing;
    private float climbCameraOffset;
    private TinyItem focusedItem;
    private TinyItem heldItem;
    private Transform itemHoldPoint;
    private float heldItemMotionTimer;
    private bool isGrabbingItem;
    private Coroutine itemGrabRoutine;
    private TinyRailWagon focusedWagon;
    private TinyRailWagon pushingWagon;
    private TinyWagonSendLever focusedSendLever;
    private bool isEndOfGameDead;
    private float currentHealth;
    private float lastDamageTime = -999f;
    private int lastLoggedHealthPercent = -1;
    private float nextHealthDebugLogTime;
    private Transform spectateTarget;

    public float CurrentPitch => pitch;
    public Quaternion CurrentCameraWorldRotation => cameraPivot != null ? cameraPivot.rotation : transform.rotation;
    public Transform HeldItemTransform => heldItem != null ? heldItem.transform : null;
    public bool IsClimbingWithHeldItem => isClimbing && heldItem != null;
    public bool IsDead => isEndOfGameDead;
    public float Health01 => maxHealth > 0f ? Mathf.Clamp01(currentHealth / maxHealth) : 0f;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (raymanBody == null)
        {
            raymanBody = GetComponent<TinyRaymanBody>();
        }

        if (raymanBody == null)
        {
            raymanBody = gameObject.AddComponent<TinyRaymanBody>();
        }

        if (cameraPivot == null && Camera.main != null)
        {
            cameraPivot = Camera.main.transform;
        }

        ConfigureTinyBody(standingHeight);
        currentEyeHeight = standingEyeHeight;
        currentHealth = maxHealth;
        LogHealthDebug("spawn", true);
        ApplyCameraHeight(true);
    }

    private void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnDisable()
    {
        climbZones.Clear();
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (isEndOfGameDead)
        {
            UpdateSpectateCamera();
            return;
        }

        RefreshHitboxOffset();

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && heldItem == null)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (isClimbing)
        {
            Look(climbLookInfluence);
            ApplyCameraHeight(false);
            return;
        }

        Look(1f);

        if (isGrabbingItem)
        {
            Move();
            UpdateCrouch();
            UpdateCameraBob();
            return;
        }

        if (pushingWagon != null)
        {
            UpdateWagonPush();
            return;
        }

        if (Mouse.current != null && heldItem != null)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                ReleaseHeldItem(false);
                return;
            }

            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                ReleaseHeldItem(true);
                return;
            }
        }

        UpdateItemFocus();
        UpdateWagonFocus();
        UpdateSendLeverFocus();

        if (Keyboard.current.eKey.wasPressedThisFrame && focusedItem != null)
        {
            PickUpItem(focusedItem);
            return;
        }

        if (Keyboard.current.eKey.wasPressedThisFrame && focusedWagon != null)
        {
            StartPushingWagon(focusedWagon);
            return;
        }

        if (Keyboard.current.eKey.wasPressedThisFrame && focusedSendLever != null)
        {
            focusedSendLever.TryActivate();
            focusedSendLever = null;
            return;
        }

        if (Keyboard.current.eKey.wasPressedThisFrame && TryGetBestClimbRoute(out ClimbZone.Route route))
        {
            StartCoroutine(ClimbTo(route));
            return;
        }

        UpdateHeldItemMotion();
        UpdateHeldItemHands();
        Move();
        UpdateCrouch();
        UpdateCameraBob();
        RegenerateHealth();
    }

    public void RegisterClimbZone(ClimbZone zone)
    {
        if (zone != null && !climbZones.Contains(zone))
        {
            climbZones.Add(zone);
        }
    }

    public void UnregisterClimbZone(ClimbZone zone)
    {
        climbZones.Remove(zone);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isEndOfGameDead)
        {
            return;
        }

        RegisterClimbZone(other.GetComponentInParent<ClimbZone>());
    }

    private void OnTriggerExit(Collider other)
    {
        UnregisterClimbZone(other.GetComponentInParent<ClimbZone>());
    }

    public void TriggerEndOfGameDeath()
    {
        if (isEndOfGameDead)
        {
            return;
        }

        isEndOfGameDead = true;
        currentHealth = 0f;
        LogHealthDebug("death", true);
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        focusedItem = null;
        focusedWagon = null;
        pushingWagon = null;

        if (itemGrabRoutine != null)
        {
            StopCoroutine(itemGrabRoutine);
            itemGrabRoutine = null;
        }

        if (heldItem != null)
        {
            ReleaseHeldItem(false);
        }

        if (controller != null)
        {
            controller.enabled = false;
        }

        if (raymanBody != null)
        {
            raymanBody.TriggerLegoBreakAndDestroy(3f);
        }

        spectateTarget = TinyNetcodeManager.GetRandomAliveSpectateTarget(transform);
        if (cameraPivot != null && spectateTarget != null)
        {
            cameraPivot.SetParent(null, true);
        }
    }

    public void TakeDamage(float damage)
    {
        if (isEndOfGameDead || damage <= 0f)
        {
            return;
        }

        currentHealth = Mathf.Max(0f, currentHealth - damage);
        lastDamageTime = Time.time;
        LogHealthDebug("damage");
        if (currentHealth <= 0f)
        {
            TriggerEndOfGameDeath();
        }
    }

    private void RegenerateHealth()
    {
        if (isEndOfGameDead || currentHealth >= maxHealth || Time.time - lastDamageTime < regenDelay)
        {
            return;
        }

        float previousHealth = currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + regenPerSecond * Time.deltaTime);
        if (!Mathf.Approximately(previousHealth, currentHealth))
        {
            LogHealthDebug("regen");
        }
    }

    private void UpdateSpectateCamera()
    {
        if (cameraPivot == null)
        {
            return;
        }

        if (spectateTarget == null)
        {
            spectateTarget = TinyNetcodeManager.GetRandomAliveSpectateTarget(transform);
            if (spectateTarget == null)
            {
                return;
            }
        }

        Vector3 targetPosition = spectateTarget.TransformPoint(spectateCameraOffset);
        Quaternion targetRotation = Quaternion.LookRotation((spectateTarget.position + Vector3.up * 0.35f - targetPosition).normalized, Vector3.up);
        float follow = 1f - Mathf.Exp(-spectateFollowSharpness * Time.deltaTime);
        cameraPivot.position = Vector3.Lerp(cameraPivot.position, targetPosition, follow);
        cameraPivot.rotation = Quaternion.Slerp(cameraPivot.rotation, targetRotation, follow);
    }

    private void OnRenderObject()
    {
        if ((!showClimbZonesInGame && focusedItem == null && focusedWagon == null && focusedSendLever == null) || !EnsureDebugLineMaterial())
        {
            return;
        }

        debugLineMaterial.SetPass(0);
        GL.PushMatrix();

        if (showClimbZonesInGame)
        {
            IReadOnlyList<ClimbZone> zones = ClimbZone.RegisteredZones;
            for (int i = 0; i < zones.Count; i++)
            {
                ClimbZone zone = zones[i];
                if (zone == null || !zone.isActiveAndEnabled)
                {
                    continue;
                }

                DrawRuntimeWireBox(zone.VolumeCollider, climbZoneDebugColor);

                if (zone.LandingZoneOverride != null)
                {
                    DrawRuntimeWireBox(zone.LandingZoneOverride, landingZoneDebugColor);
                }
            }
        }

        if (focusedItem != null)
        {
            DrawRuntimeWireBounds(focusedItem.GetWorldBounds(), itemHighlightColor);
        }

        if (focusedWagon != null)
        {
            DrawRuntimeWireBounds(focusedWagon.GetWorldBounds(), Color.cyan);
        }

        if (focusedSendLever != null)
        {
            DrawRuntimeWireBounds(focusedSendLever.GetWorldBounds(), Color.yellow);
        }

        GL.PopMatrix();
    }

    private void OnGUI()
    {
        DrawDamageOverlay();

        if (focusedItem == null && focusedWagon == null && focusedSendLever == null)
        {
            return;
        }

        const float width = 240f;
        const float height = 112f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.58f, width, height);

        GUI.Box(rect, string.Empty);
        if (focusedItem != null)
        {
            GUI.Label(new Rect(rect.x + 14f, rect.y + 10f, width - 28f, 22f), focusedItem.ItemName);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 36f, width - 28f, 20f), "Poids : " + focusedItem.WeightKilograms.ToString("0.##") + " kg");
            GUI.Label(new Rect(rect.x + 14f, rect.y + 58f, width - 28f, 20f), "Prix : " + focusedItem.Value + " $");
            GUI.Label(new Rect(rect.x + 14f, rect.y + 84f, width - 28f, 20f), "[E] Prendre");
        }
        else if (focusedWagon != null)
        {
            GUI.Label(new Rect(rect.x + 14f, rect.y + 16f, width - 28f, 24f), "Wagon");
            GUI.Label(new Rect(rect.x + 14f, rect.y + 54f, width - 28f, 24f), "[E] Pousser");
        }
        else
        {
            GUI.Label(new Rect(rect.x + 14f, rect.y + 16f, width - 28f, 24f), "Levier");
            GUI.Label(new Rect(rect.x + 14f, rect.y + 54f, width - 28f, 24f), "[E] Appuyer pour envoyer");
        }
    }

    private void DrawDamageOverlay()
    {
        if (!isEndOfGameDead)
        {
            Texture2D damageTexture = GetDamageOverlayTexture();
            if (damageTexture != null)
            {
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), damageTexture, ScaleMode.StretchToFill, true);
            }
        }

        if (isEndOfGameDead)
        {
            Color previousColor = GUI.color;
            GUI.color = deadScreenTint;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }

    private Texture2D GetDamageOverlayTexture()
    {
        int stage = GetDamageOverlayStage();
        if (stage < 0)
        {
            return null;
        }

        if (damageScreenStages != null && stage < damageScreenStages.Length && damageScreenStages[stage] != null)
        {
            return damageScreenStages[stage];
        }

        return damageScreenStageSprites != null && stage < damageScreenStageSprites.Length && damageScreenStageSprites[stage] != null
            ? damageScreenStageSprites[stage].texture
            : null;
    }

    private int GetDamageOverlayStage()
    {
        if (maxHealth <= 0f || currentHealth >= maxHealth)
        {
            return -1;
        }

        float healthPercent = Health01 * 100f;
        return healthPercent > 80f ? 0
            : healthPercent > 60f ? 1
            : healthPercent > 40f ? 2
            : healthPercent > 20f ? 3
            : 4;
    }

    private void LogHealthDebug(string reason, bool force = false)
    {
        if (!debugLogHealth || maxHealth <= 0f)
        {
            return;
        }

        int healthPercent = Mathf.RoundToInt(Health01 * 100f);
        if (!force && healthPercent == lastLoggedHealthPercent && Time.time < nextHealthDebugLogTime)
        {
            return;
        }

        lastLoggedHealthPercent = healthPercent;
        nextHealthDebugLogTime = Time.time + 0.25f;
        int stage = GetDamageOverlayStage();
        string stageText = stage >= 0 ? "niveau " + (stage + 1) : "aucun";
        Debug.Log("Tiny Health [" + name + "] " + currentHealth.ToString("0.0") + "/" + maxHealth.ToString("0.0")
            + " (" + healthPercent + "%), ecran: " + stageText + ", raison: " + reason);
    }

    private void OnDrawGizmos()
    {
        if (!showClimbZonesInGame)
        {
            return;
        }

        Color climbFill = WithAlpha(climbZoneDebugColor, 0.22f);
        Color climbWire = WithAlpha(climbZoneDebugColor, 0.9f);
        Color landingFill = WithAlpha(landingZoneDebugColor, 0.22f);
        Color landingWire = WithAlpha(landingZoneDebugColor, 0.9f);

        ClimbZone[] zones = Object.FindObjectsByType<ClimbZone>(FindObjectsSortMode.None);
        for (int i = 0; i < zones.Length; i++)
        {
            ClimbZone zone = zones[i];
            if (zone != null && zone.isActiveAndEnabled)
            {
                zone.DrawGizmosDebug(climbFill, climbWire, landingFill, landingWire);
            }
        }
    }

    private void Look(float sensitivityMultiplier)
    {
        if (Mouse.current == null || cameraPivot == null || Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();
        transform.Rotate(Vector3.up * (mouseDelta.x * EffectiveSensitivity * sensitivityMultiplier));

        pitch = Mathf.Clamp(pitch - mouseDelta.y * EffectiveSensitivity * sensitivityMultiplier, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        if (raymanBody != null)
        {
            raymanBody.SetCameraLook(pitch, cameraPivot.rotation);
        }
    }

    private void Move()
    {
        Vector2 input = ReadMoveInput();
        Vector3 targetDirection = transform.right * input.x + transform.forward * input.y;
        targetDirection = Vector3.ClampMagnitude(targetDirection, 1f);

        bool isCrouching = IsCrouchHeld();
        float targetSpeed = isCrouching ? crouchSpeed : IsSprintHeld() ? sprintSpeed : walkSpeed;
        targetSpeed *= GetCarrySpeedMultiplier();
        Vector3 targetVelocity = targetDirection * targetSpeed;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);

        if (controller.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -1.5f;
        }

        if (controller.isGrounded && !isCrouching && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            if (raymanBody != null)
            {
                raymanBody.NotifyJump();
            }
        }

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private bool TryGetBestClimbRoute(out ClimbZone.Route bestRoute)
    {
        bestRoute = default;
        if (!CanClimbWithHeldItem())
        {
            return false;
        }

        float bestScore = float.PositiveInfinity;

        for (int i = climbZones.Count - 1; i >= 0; i--)
        {
            ClimbZone zone = climbZones[i];
            if (zone == null)
            {
                climbZones.RemoveAt(i);
                continue;
            }

            if (!zone.ContainsPlayer(transform.position, controller))
            {
                climbZones.RemoveAt(i);
                continue;
            }

            if (!zone.TryGetRoute(transform.position, controller, out ClimbZone.Route route))
            {
                continue;
            }

            if (!CanClimbFromCurrentFacing(route))
            {
                continue;
            }

            if (!HasLandingClearance(route.LandingPosition))
            {
                continue;
            }

            float score = (route.GrabPosition - transform.position).sqrMagnitude
                + (route.LandingPosition - transform.position).sqrMagnitude * 0.15f;

            if (score < bestScore)
            {
                bestScore = score;
                bestRoute = route;
            }
        }

        return bestScore < float.PositiveInfinity;
    }

    private bool CanClimbFromCurrentFacing(ClimbZone.Route route)
    {
        Vector3 climbDirection = Vector3.ProjectOnPlane(route.LandingPosition - transform.position, Vector3.up);
        Vector3 lookDirection = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

        if (climbDirection.sqrMagnitude < 0.0001f || lookDirection.sqrMagnitude < 0.0001f)
        {
            return true;
        }

        float angle = Vector3.Angle(lookDirection, climbDirection);
        return angle <= maxClimbFacingAngle;
    }

    private bool HasLandingClearance(Vector3 feetPosition)
    {
        float radius = controller.radius * 0.9f;
        Vector3 bottom = feetPosition + Vector3.up * (radius + 0.02f);
        Vector3 top = feetPosition + Vector3.up * (standingHeight - radius);

        Collider[] hits = Physics.OverlapCapsule(bottom, top, radius, landingClearanceMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            if (!hits[i].transform.IsChildOf(transform))
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerator ClimbTo(ClimbZone.Route route)
    {
        bool oneHandItemClimb = IsHoldingItemWithOneActiveHand();
        float activeClimbDuration = oneHandItemClimb
            ? climbDuration / Mathf.Max(0.01f, oneHandItemClimbSpeedMultiplier)
            : climbDuration;

        isClimbing = true;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        bobTimer = 0f;
        bobOffset = 0f;
        climbCameraOffset = 0f;
        currentEyeHeight = standingEyeHeight;
        ConfigureTinyBody(standingHeight);
        if (raymanBody != null)
        {
            AttachHandsForClimb(route);
        }

        controller.enabled = false;

        Vector3 start = transform.position;
        Vector3 grab = route.GrabPosition;
        Vector3 landing = route.LandingPosition;
        Vector3 liftPoint = new Vector3(grab.x, Mathf.Max(grab.y, landing.y) + climbLiftHeight, grab.z);
        float elapsed = 0f;

        while (elapsed < activeClimbDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / activeClimbDuration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            if (t < 0.42f)
            {
                transform.position = Vector3.Lerp(start, grab, SmootherStep(t / 0.42f));
            }
            else if (t < 0.72f)
            {
                transform.position = Vector3.Lerp(grab, liftPoint, SmootherStep((t - 0.42f) / 0.3f));
            }
            else
            {
                transform.position = Vector3.Lerp(liftPoint, landing, SmootherStep((t - 0.72f) / 0.28f));
            }

            climbCameraOffset = -Mathf.Sin(eased * Mathf.PI) * climbCameraPull;
            AttachHandsForClimb(route);
            yield return null;
        }

        transform.position = landing;
        controller.enabled = true;
        climbCameraOffset = 0f;
        currentEyeHeight = standingEyeHeight;
        ApplyCameraHeight(true);
        if (raymanBody != null)
        {
            if (heldItem != null)
            {
                UpdateHeldItemHands();
            }
            else
            {
                raymanBody.ReleaseHands();
            }
        }

        isClimbing = false;
    }

    private bool CanClimbWithHeldItem()
    {
        return heldItem == null || GetActiveHeldItemHandCount() == 1;
    }

    private bool IsHoldingItemWithOneActiveHand()
    {
        return heldItem != null && GetActiveHeldItemHandCount() == 1;
    }

    private int GetActiveHeldItemHandCount()
    {
        if (heldItem == null)
        {
            return 0;
        }

        int count = 0;
        if (heldItem.LeftHandActive)
        {
            count++;
        }

        if (heldItem.RightHandActive)
        {
            count++;
        }

        return count;
    }

    private void AttachHandsForClimb(ClimbZone.Route route)
    {
        if (raymanBody == null)
        {
            return;
        }

        if (!IsHoldingItemWithOneActiveHand())
        {
            raymanBody.AttachHandsWithLocalOffsets(route.LeftHandAnchor, route.RightHandAnchor, route.LeftHandRotation, route.RightHandRotation);
            return;
        }

        heldItem.GetHandAnchors(transform, out Vector3 itemLeftAnchor, out Vector3 itemRightAnchor);
        heldItem.GetHandRotations(transform, out Quaternion itemLeftRotation, out Quaternion itemRightRotation);
        RemoveItemHandGripLift(heldItem, ref itemLeftAnchor, ref itemRightAnchor);

        bool itemUsesLeftHand = heldItem.LeftHandActive;
        bool itemUsesRightHand = heldItem.RightHandActive;
        Vector3 leftAnchor = itemUsesLeftHand ? itemLeftAnchor : route.LeftHandAnchor;
        Vector3 rightAnchor = itemUsesRightHand ? itemRightAnchor : route.RightHandAnchor;
        Quaternion leftRotation = itemUsesLeftHand
            ? itemLeftRotation
            : raymanBody.GetLeftAttachedHandRotation(route.LeftHandRotation);
        Quaternion rightRotation = itemUsesRightHand
            ? itemRightRotation
            : raymanBody.GetRightAttachedHandRotation(route.RightHandRotation);

        raymanBody.AttachHands(leftAnchor, rightAnchor, leftRotation, rightRotation);
    }

    private void RemoveItemHandGripLift(TinyItem item, ref Vector3 leftAnchor, ref Vector3 rightAnchor)
    {
        if (item == null || raymanBody == null)
        {
            return;
        }

        Vector3 lift = Vector3.up * raymanBody.HandGripLift;
        if (item.LeftHandActive)
        {
            leftAnchor -= lift;
        }

        if (item.RightHandActive)
        {
            rightAnchor -= lift;
        }
    }

    private static float SmootherStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private void UpdateCrouch()
    {
        bool isCrouching = IsCrouchHeld();
        float targetHeight = isCrouching ? crouchingHeight : standingHeight;
        float targetEyeHeight = isCrouching ? crouchingEyeHeight : standingEyeHeight;

        ConfigureTinyBody(Mathf.Lerp(controller.height, targetHeight, crouchLerpSpeed * Time.deltaTime));
        currentEyeHeight = Mathf.Lerp(currentEyeHeight, targetEyeHeight, crouchLerpSpeed * Time.deltaTime);
    }

    private void UpdateCameraBob()
    {
        if (cameraPivot == null)
        {
            return;
        }

        bool isMoving = ReadMoveInput().sqrMagnitude > 0.01f && horizontalVelocity.sqrMagnitude > 0.01f;
        bool isCrouching = IsCrouchHeld();
        bool isGrounded = controller.isGrounded;

        if (isMoving && isGrounded)
        {
            float bobSpeed = isCrouching ? crouchBobSpeed : IsSprintHeld() ? sprintBobSpeed : walkBobSpeed;
            float bobAmount = isCrouching ? crouchBobAmount : IsSprintHeld() ? sprintBobAmount : walkBobAmount;

            bobTimer += Time.deltaTime * bobSpeed;
            bobOffset = Mathf.Sin(bobTimer) * bobAmount;
        }
        else
        {
            bobTimer = 0f;
            bobOffset = Mathf.Lerp(bobOffset, 0f, bobReturnSpeed * Time.deltaTime);
        }

        ApplyCameraHeight(false);
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
        RefreshHitboxOffset(height);
        controller.stepOffset = 0.12f;
    }

    private void RefreshHitboxOffset()
    {
        RefreshHitboxOffset(controller != null ? controller.height : standingHeight);
    }

    private void RefreshHitboxOffset(float height)
    {
        if (controller == null)
        {
            return;
        }

        Vector3 hitboxOffset = (raymanBody != null ? raymanBody.HitboxLocalOffset : Vector3.zero) + manualHitboxCenterOffset;
        controller.center = Vector3.up * (height * 0.5f) + hitboxOffset;
    }

    private void ApplyCameraHeight(bool snap)
    {
        if (cameraPivot == null)
        {
            return;
        }

        Vector3 targetPosition = new Vector3(0f, currentEyeHeight + bobOffset + climbCameraOffset, cameraForwardOffset)
            + manualCameraLocalOffset;
        cameraPivot.localPosition = snap
            ? targetPosition
            : Vector3.Lerp(cameraPivot.localPosition, targetPosition, crouchLerpSpeed * Time.deltaTime);
    }

    private void UpdateItemFocus()
    {
        focusedItem = null;
        if (cameraPivot == null || heldItem != null || pushingWagon != null)
        {
            return;
        }

        Ray ray = new Ray(cameraPivot.position, cameraPivot.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, itemLookDistance, itemInteractionMask, QueryTriggerInteraction.Collide))
        {
            return;
        }

        TinyItem item = hit.collider.GetComponentInParent<TinyItem>();
        if (item != null && !item.IsNetworkHeld)
        {
            focusedItem = item;
        }
    }

    private void UpdateWagonFocus()
    {
        focusedWagon = null;
        if (cameraPivot == null || heldItem != null || focusedItem != null || pushingWagon != null)
        {
            return;
        }

        Ray ray = new Ray(cameraPivot.position, cameraPivot.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, wagonLookDistance, wagonInteractionMask, QueryTriggerInteraction.Collide))
        {
            return;
        }

        TinyRailWagon wagon = hit.collider.GetComponentInParent<TinyRailWagon>();
        if (wagon != null && wagon.CanInteract(transform))
        {
            focusedWagon = wagon;
        }
    }

    private void UpdateSendLeverFocus()
    {
        focusedSendLever = null;
        if (cameraPivot == null || heldItem != null || focusedItem != null || focusedWagon != null || pushingWagon != null)
        {
            return;
        }

        Ray ray = new Ray(cameraPivot.position, cameraPivot.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, wagonLookDistance, wagonInteractionMask, QueryTriggerInteraction.Collide))
        {
            return;
        }

        TinyWagonSendLever lever = hit.collider.GetComponentInParent<TinyWagonSendLever>();
        if (lever != null && lever.CanInteract(transform))
        {
            focusedSendLever = lever;
        }
    }

    private void PickUpItem(TinyItem item)
    {
        if (item == null || cameraPivot == null || isGrabbingItem)
        {
            return;
        }

        if (itemGrabRoutine != null)
        {
            StopCoroutine(itemGrabRoutine);
        }

        itemGrabRoutine = StartCoroutine(GrabItemRoutine(item));
    }

    private IEnumerator GrabItemRoutine(TinyItem item)
    {
        isGrabbingItem = true;
        focusedItem = null;
        focusedWagon = null;

        EnsureItemHoldPoint();
        item.GetHandAnchors(transform, out Vector3 leftAnchor, out Vector3 rightAnchor);
        item.GetHandRotations(transform, out Quaternion leftRotation, out Quaternion rightRotation);
        RemoveItemHandGripLift(item, ref leftAnchor, ref rightAnchor);

        float handElapsed = 0f;
        while (handElapsed < itemGrabHandDuration)
        {
            handElapsed += Time.deltaTime;
            if (raymanBody != null)
            {
                raymanBody.AttachHands(
                    leftAnchor,
                    rightAnchor,
                    leftRotation,
                    rightRotation,
                    item.LeftHandActive,
                    item.RightHandActive);
            }

            yield return null;
        }

        item.GetHoldLocalPose(out Vector3 holdLocalPosition, out Quaternion holdLocalRotation);
        Quaternion holdStartRotation = item.transform.rotation * Quaternion.Inverse(holdLocalRotation);
        Vector3 holdStartPosition = item.transform.position - holdStartRotation * holdLocalPosition;
        itemHoldPoint.SetPositionAndRotation(holdStartPosition, holdStartRotation);
        item.PickUp(itemHoldPoint, true);
        heldItem = item;
        heldItemMotionTimer = 0f;
        TinyNetcodeManager.TrySendItemPickup(item.transform);

        Vector3 pullStartLocalPosition = itemHoldPoint.localPosition;
        Quaternion pullStartLocalRotation = itemHoldPoint.localRotation;
        float pullElapsed = 0f;
        float pullDuration = Mathf.Max(0.01f, itemGrabPullDuration);
        while (pullElapsed < pullDuration)
        {
            pullElapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(pullElapsed / pullDuration));
            itemHoldPoint.localPosition = Vector3.Lerp(pullStartLocalPosition, heldItemLocalPosition, t);
            itemHoldPoint.localRotation = Quaternion.Slerp(pullStartLocalRotation, Quaternion.identity, t);
            UpdateHeldItemHands();
            yield return null;
        }

        itemHoldPoint.localPosition = heldItemLocalPosition;
        itemHoldPoint.localRotation = Quaternion.identity;
        UpdateHeldItemMotion(true);
        UpdateHeldItemHands();
        isGrabbingItem = false;
        itemGrabRoutine = null;
    }

    private void ReleaseHeldItem(bool throwItem)
    {
        if (heldItem == null)
        {
            return;
        }

        TinyItem item = heldItem;
        heldItem = null;
        focusedItem = null;

        Vector3 dropPosition = item.transform.position;
        Quaternion dropRotation = item.transform.rotation;
        if (throwItem)
        {
            Vector3 throwDirection = cameraPivot != null ? cameraPivot.forward : transform.forward;
            float force = itemThrowForce / (1f + item.WeightKilograms * itemThrowWeightSlowdown);
            Vector3 throwVelocity = throwDirection.normalized * force;
            TinyNetcodeManager.TrySendItemRelease(item.transform, dropPosition, dropRotation, throwVelocity);
            item.Throw(dropPosition, dropRotation, throwVelocity);
        }
        else
        {
            TinyNetcodeManager.TrySendItemRelease(item.transform, dropPosition, dropRotation, Vector3.zero);
            item.Drop(dropPosition, dropRotation);
        }

        if (raymanBody != null)
        {
            raymanBody.ReleaseHands();
        }
    }

    private void EnsureItemHoldPoint()
    {
        if (itemHoldPoint != null)
        {
            return;
        }

        GameObject holdPointObject = new GameObject("__Item_Hold_Point");
        itemHoldPoint = holdPointObject.transform;
        itemHoldPoint.SetParent(cameraPivot, false);
        itemHoldPoint.localPosition = heldItemLocalPosition;
        itemHoldPoint.localRotation = Quaternion.identity;
        itemHoldPoint.localScale = Vector3.one;
    }

    private void UpdateHeldItemMotion(bool snap = false)
    {
        if (heldItem == null || itemHoldPoint == null)
        {
            return;
        }

        Vector2 moveInput = ReadMoveInput();
        bool isMoving = moveInput.sqrMagnitude > 0.01f && horizontalVelocity.sqrMagnitude > 0.01f && controller.isGrounded;
        float speed = isMoving ? IsSprintHeld() ? sprintBobSpeed : walkBobSpeed : heldItemIdleBobSpeed;
        heldItemMotionTimer += Time.deltaTime * speed;

        float weightDampen = 1f / (1f + heldItem.WeightKilograms * 0.08f);
        float bobAmount = isMoving ? heldItemBobAmount : heldItemIdleBobAmount;
        float wave = Mathf.Sin(heldItemMotionTimer);
        Vector3 targetLocalPosition = heldItemLocalPosition
            + Vector3.up * (wave * bobAmount * weightDampen);

        float follow = 1f - Mathf.Exp(-heldItemMotionSharpness * Time.deltaTime);
        itemHoldPoint.localPosition = snap
            ? targetLocalPosition
            : Vector3.Lerp(itemHoldPoint.localPosition, targetLocalPosition, follow);
    }

    private void UpdateHeldItemHands()
    {
        if (heldItem == null || raymanBody == null)
        {
            return;
        }

        heldItem.GetHandAnchors(transform, out Vector3 leftAnchor, out Vector3 rightAnchor);
        heldItem.GetHandRotations(transform, out Quaternion leftRotation, out Quaternion rightRotation);
        RemoveItemHandGripLift(heldItem, ref leftAnchor, ref rightAnchor);
        raymanBody.AttachHands(
            leftAnchor,
            rightAnchor,
            leftRotation,
            rightRotation,
            heldItem.LeftHandActive,
            heldItem.RightHandActive,
            true);
    }

    private void StartPushingWagon(TinyRailWagon wagon)
    {
        if (wagon == null || heldItem != null || !TinyNetcodeManager.CanUseWagonSide(wagon.transform, transform))
        {
            return;
        }

        pushingWagon = wagon;
        focusedWagon = null;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        if (TinyNetcodeManager.IsNetworkActive)
        {
            if (!TinyNetcodeManager.TrySendWagonGrab(pushingWagon.transform, transform.position, transform.rotation))
            {
                pushingWagon = null;
                return;
            }
        }

        AttachHandsToWagon();
    }

    private void StopPushingWagon()
    {
        TinyRailWagon releasedWagon = pushingWagon;
        if (releasedWagon != null && TinyNetcodeManager.IsNetworkActive)
        {
            TinyNetcodeManager.TrySendWagonRelease(releasedWagon.transform, transform.position, transform.rotation);
        }

        pushingWagon = null;
        if (raymanBody != null)
        {
            raymanBody.ReleaseHands();
        }
    }

    public void ForceReleaseWagon(TinyRailWagon wagon)
    {
        if (pushingWagon == null || (wagon != null && pushingWagon != wagon))
        {
            return;
        }

        StopPushingWagon();
    }

    private void UpdateWagonPush()
    {
        if (pushingWagon == null)
        {
            return;
        }

        if ((Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            || (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame))
        {
            StopPushingWagon();
            return;
        }

        Vector2 input = ReadMoveInput();
        Vector3 wagonDelta = Vector3.zero;
        if (TinyNetcodeManager.IsNetworkActive)
        {
            TinyNetcodeManager.TrySendWagonPush(pushingWagon.transform, input.y, transform.position, transform.rotation);
        }
        else
        {
            wagonDelta = pushingWagon.PushFromPlayer(transform, input.y, Time.deltaTime);
        }

        if (wagonDelta.sqrMagnitude > 0.000001f)
        {
            controller.Move(wagonDelta);
        }

        Vector3 targetPosition = pushingWagon.GetPlayerFollowPosition(transform);
        Vector3 followDelta = targetPosition - transform.position;
        followDelta.y = 0f;
        float follow = 1f - Mathf.Exp(-wagonPlayerFollowSharpness * Time.deltaTime);
        controller.Move(followDelta * follow);

        if (controller.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -1.5f;
        }

        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
        UpdateCameraBob();
        AttachHandsToWagon();
    }

    private void AttachHandsToWagon()
    {
        if (pushingWagon == null || raymanBody == null)
        {
            return;
        }

        pushingWagon.GetHandAnchors(transform, out Vector3 leftAnchor, out Vector3 rightAnchor);
        pushingWagon.GetHandRotations(transform, out Quaternion leftRotation, out Quaternion rightRotation);
        raymanBody.AttachHands(leftAnchor, rightAnchor, leftRotation, rightRotation);
    }

    private float GetCarrySpeedMultiplier()
    {
        if (heldItem == null)
        {
            return 1f;
        }

        float multiplier = 1f / (1f + heldItem.WeightKilograms * carryWeightSlowdown);
        return Mathf.Max(minimumCarrySpeedMultiplier, multiplier);
    }

    private static bool EnsureDebugLineMaterial()
    {
        if (debugLineMaterial != null)
        {
            return true;
        }

        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            return false;
        }

        debugLineMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        debugLineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        debugLineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        debugLineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        debugLineMaterial.SetInt("_ZWrite", 0);
        return true;
    }

    private static void DrawRuntimeWireBox(BoxCollider box, Color color)
    {
        if (box == null || !box.enabled)
        {
            return;
        }

        Matrix4x4 matrix = box.transform.localToWorldMatrix;
        Vector3 center = box.center;
        Vector3 extents = box.size * 0.5f;

        Vector3 p0 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, -extents.y, -extents.z));
        Vector3 p1 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, -extents.y, -extents.z));
        Vector3 p2 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, -extents.y, extents.z));
        Vector3 p3 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, -extents.y, extents.z));
        Vector3 p4 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, extents.y, -extents.z));
        Vector3 p5 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, extents.y, -extents.z));
        Vector3 p6 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, extents.y, extents.z));
        Vector3 p7 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, extents.y, extents.z));

        GL.Begin(GL.LINES);
        GL.Color(color);
        DrawLine(p0, p1);
        DrawLine(p1, p2);
        DrawLine(p2, p3);
        DrawLine(p3, p0);
        DrawLine(p4, p5);
        DrawLine(p5, p6);
        DrawLine(p6, p7);
        DrawLine(p7, p4);
        DrawLine(p0, p4);
        DrawLine(p1, p5);
        DrawLine(p2, p6);
        DrawLine(p3, p7);
        GL.End();
    }

    private static void DrawRuntimeWireBounds(Bounds bounds, Color color)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3 p0 = new Vector3(min.x, min.y, min.z);
        Vector3 p1 = new Vector3(max.x, min.y, min.z);
        Vector3 p2 = new Vector3(max.x, min.y, max.z);
        Vector3 p3 = new Vector3(min.x, min.y, max.z);
        Vector3 p4 = new Vector3(min.x, max.y, min.z);
        Vector3 p5 = new Vector3(max.x, max.y, min.z);
        Vector3 p6 = new Vector3(max.x, max.y, max.z);
        Vector3 p7 = new Vector3(min.x, max.y, max.z);

        GL.Begin(GL.LINES);
        GL.Color(color);
        DrawLine(p0, p1);
        DrawLine(p1, p2);
        DrawLine(p2, p3);
        DrawLine(p3, p0);
        DrawLine(p4, p5);
        DrawLine(p5, p6);
        DrawLine(p6, p7);
        DrawLine(p7, p4);
        DrawLine(p0, p4);
        DrawLine(p1, p5);
        DrawLine(p2, p6);
        DrawLine(p3, p7);
        GL.End();
    }

    private static void DrawLine(Vector3 start, Vector3 end)
    {
        GL.Vertex(start);
        GL.Vertex(end);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }
}
