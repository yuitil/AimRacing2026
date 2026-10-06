/**
 * @file    G923_FFBController.cs
 * @brief   FFBの制御(G923対応)
 * @author  中田樹希 / 中里優太（共同制作）
 * @date    2026/07/28
 *
 * ---------------------------------------------------------------
 * 担当範囲
 * ---------------------------------------------------------------
 *  中田 樹希
 *   ・Logitech SDK 初期化との連携、USB 切断・再接続・Function5 復帰処理
 *   ・Input System / PlayerInput の再同期と再ペアリング
 *   ・センタリング（Spring）とハンドルの重さ（Damper）
 *   ・Master Gain、操作範囲の設定、最終的なパラメータ調整
 *
 *  中里 優太
 *   ・路面振動：3 種類の路面エフェクトをランダムに発生させる処理(1357行あたり)
 *              （速度に応じて発生頻度・強さ・長さが変化）
 *   ・壁衝突 FFB：接触した側に応じてハンドルを取られる反動と、その減衰(1445行あたり)
 *
 *  ※ 各セクション見出しの [担当: ○○] で、どちらが実装したかを示す
 * ---------------------------------------------------------------
 */

// 使用するときはGameManager側でLogitech SDKの初期化処理を有効にしてください。
// Editor上でFFBを確認する場合は、GameManager側でもEditor中に
// LogitechGSDK.LogiSteeringInitialize(false) を呼ぶ必要があります。

// 統合内容
// ・SpringForceでセンタリングを制御
// ・DamperForceでハンドル全体の重さを制御
// ・ConstantForceは通常センタリング用途では使用しない
// ・USB切断時のFFB停止、一時停止、接続状態通知
// ・再接続後、Function5入力待ちにしてから復帰
// ・復帰時にPlayerInput / InputActionAssetを再有効化
// ・復帰時にInput System側のG923/Joystickを再同期
// ・復帰時にLogitech SDKを再初期化
// ・仮の路面凹凸FFB
// ・速度に応じた凹凸FFBの強度・頻度調整
// ・壁衝突時のFFB強度制限と短時間の衝撃FFB
// ・FFB全体のMaster Gain
// ・Editor上でもFFBを有効化できる設定
// ・シーン遷移や無効化時にFFBが残らないようStopAllFFBを実装

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.InputSystem.Controls;

public class G923_FFBController : MonoBehaviour
{
    // ==============================
    // 外部通知
    // ==============================

    /// <summary>
    /// G923の接続状態が変化した時に通知する
    /// true  = 接続
    /// false = 切断
    /// </summary>
    public static event Action<bool> OnG923ConnectionChanged;

    /// <summary>
    /// G923の復帰状態
    /// </summary>
    public enum EG923ResumeState
    {
        Connected,
        Disconnected,
        WaitingForFunction5,
    }

    /// <summary>
    /// G923復帰状態が変化した時にUIへ通知する
    /// </summary>
    public static event Action<EG923ResumeState> OnG923ResumeStateChanged;

    // ==============================
    // マスター設定
    // ==============================

    [Header("マスター設定")]

    [SerializeField, Tooltip("FFB全体の有効/無効スイッチ")]
    private bool m_enableMasterFFB = true;

    [SerializeField, Range(0f, 1f), Tooltip("FFB全体の出力倍率。0.5で全体の強さが50%")]
    private float m_masterGain = 1.0f;

    // ==============================
    // 車体参照
    // ==============================

    [Header("車体参照")]

    [SerializeField]
    private GameObject m_vehicle;

    [SerializeField]
    private VehicleController m_vehicleController;

    // ==============================
    // Logitech SDK設定
    // ==============================

    [Header("Logitech SDK設定")]

    [SerializeField, Tooltip("通常は0番のデバイスを使用する")]
    private int m_deviceIndex = 0;

    //G923の片側180度を基準に操作感を調整するための左右合計回転範囲
    [SerializeField, Range(90, 900), Tooltip("G923の左右を合わせた操作範囲です。360で片側180度になり、90から900まで調整できます。")]
    private int m_steeringOperatingRange = 360;

    //現在の接続でG923の操作範囲を適用済みか保持する状態
    private bool m_steeringOperatingRangeApplied = false;

    [SerializeField, Tooltip("Editor上でもFFBを実行するか")]
    private bool m_enableFFBInEditor = true;

    // ==============================
    // USB切断時の設定
    // ==============================

    [Header("USB切断時の設定")]

    [SerializeField, Tooltip("USB切断時にゲームを一時停止するか")]
    private bool m_pauseGameOnDisconnect = true;

    [SerializeField, Tooltip("再接続時に自動でゲームを再開するか。Function5復帰を使う場合はOFF推奨")]
    private bool m_resumeGameOnReconnect = false;

    private bool m_pausedByG923Disconnect = false;
    private float m_timeScaleBeforeG923Pause = 1.0f;

    // ==============================
    // 復帰操作
    // ==============================

    [Header("復帰操作")]

    [SerializeField, Tooltip("再接続後、Function5を押すまで復帰させない")]
    private bool m_requireFunction5ToResume = true;

    [SerializeField, Tooltip("Function5に割り当てたInputActionを指定する")]
    private InputActionReference m_resumeAction;

    [SerializeField, Tooltip("復帰時にPlayerInputを再有効化する")]
    private bool m_reactivatePlayerInputOnResume = true;

    [SerializeField, Tooltip("PlayerInput参照。未設定なら自動検索する")]
    private PlayerInput m_playerInput;

    [SerializeField, Tooltip("復帰入力をG923デバイスからの入力のみに制限する")]
    private bool m_resumeOnlyG923Device = true;

    [SerializeField, Tooltip("復帰後に切り替える操作用ActionMap名")]
    private string m_gameplayActionMapName = "InGame";

    [SerializeField, Tooltip("復帰時に必ず走行用ActionMapへ切り替える")]
    private bool m_switchToGameplayActionMapOnResume = true;

    [SerializeField, Tooltip("Project-wide Actionsで使っているInputActionAsset。Aim2024Inputを入れる")]
    private InputActionAsset m_inputActionsAsset;

    [SerializeField, Tooltip("復帰後も有効化しておくシステム用ActionMap名")]
    private string m_systemActionMapName = "MainSystem";

    [SerializeField, Tooltip("復帰時にLogitech SDKを再初期化する")]
    private bool m_reinitializeLogitechSDKOnResume = true;

    [SerializeField, Tooltip("復帰時にInput System側のG923/Joystickデバイスを再同期する")]
    private bool m_refreshInputDevicesOnResume = true;

    [SerializeField, Tooltip("復帰時にInput System側のJoystick/G923デバイスをリセットする")]
    private bool m_resetInputSystemDevicesOnResume = true;

    [SerializeField, Tooltip("再接続直後、入力デバイス再同期まで待つ時間 秒")]
    private float m_inputRefreshDelayAfterReconnect = 0.5f;

    [SerializeField, Tooltip("開発用。キーボードのF5でも復帰できるようにする。Rキーは許可しない")]
    private bool m_allowKeyboardF5Resume = true;

    private bool m_waitingForFunction5Resume = false;
    private Coroutine m_resumeInputCoroutine;

    // ==============================
    // 仮の路面凹凸設定 [担当: 中里]
    // ==============================

    [Header("仮の路面凹凸設定")]

    [SerializeField, Tooltip("仮の凹凸振動を有効にするか")]
    private bool m_enableFakeBump = true;

    [SerializeField, Range(0, 100), Tooltip("振動の強さ 最小")]
    private int m_fakeBumpMagnitudeMin = 10;

    [SerializeField, Range(0, 100), Tooltip("振動の強さ 最大")]
    private int m_fakeBumpMagnitudeMax = 20;

    [SerializeField, Tooltip("振動が発生するまでの最短間隔 秒")]
    private float m_bumpIntervalMin = 0.5f;

    [SerializeField, Tooltip("振動が発生するまでの最長間隔 秒")]
    private float m_bumpIntervalMax = 2.0f;

    [SerializeField, Tooltip("1回の振動が継続する基本時間 秒")]
    private float m_bumpDuration = 0.15f;

    private float m_bumpWaitTimer = 0.0f;
    private float m_bumpActiveTimer = 0.0f;
    private bool m_isBumping = false;

    // ==============================
    // 衝突時のFFB設定 [担当: 中里]
    // ==============================

    [Header("衝突時のFFB設定")]

    [SerializeField, Tooltip("衝突時FFBを有効にするか")]
    private bool m_enableCrashFFB = true;

    [SerializeField, Range(0, 100), Tooltip("衝突時にハンドルを振る力の最小値")]
    private int m_minCrashForce = 5;

    [SerializeField, Range(0, 100), Tooltip("衝突時にハンドルを振る力の最大値")]
    private int m_maxCrashForce = 20;

    [SerializeField, Tooltip("衝突によるハンドルの振れが継続する時間 秒")]
    private float m_crashDuration = 0.18f;

    [SerializeField, Tooltip("この速度未満の衝突では衝突FFBを出さない")]
    private float m_minCrashImpactSpeed = 1.5f;

    [SerializeField, Tooltip("衝突時のハンドルの振れ方向を反転する")]
    private bool m_invertCrashForce = false;

    private float m_crashActiveTimer = 0.0f;
    private int m_currentCrashForce = 0;

    // ==============================
    // 低速時のFFB設定 [担当: 中田]
    // ==============================

    [Header("低速時のFFB設定")]

    [SerializeField]
    private float m_lowSpeedThreshold = 3.0f;

    [SerializeField, Range(0, 100)]
    private int m_lowSpeedSpringSaturation = 45;

    [SerializeField, Range(0, 100)]
    private int m_lowSpeedSpringCoefficient = 40;

    [SerializeField, Range(0, 100)]
    private int m_lowSpeedDamperForce = 20;

    // ==============================
    // 走行中のセンタリング設定 [担当: 中田]
    // ==============================

    [Header("走行中のセンタリング設定")]

    [SerializeField]
    private float m_springSpeedReference = 120.0f;

    [SerializeField, Range(0, 100)]
    private int m_minSpringSaturation = 60;

    [SerializeField, Range(0, 100)]
    private int m_maxSpringSaturation = 100;

    [SerializeField, Range(0, 100)]
    private int m_minSpringCoefficient = 50;

    [SerializeField, Range(0, 100)]
    private int m_maxSpringCoefficient = 95;

    // ==============================
    // ダンパー設定 [担当: 中田]
    // ==============================

    [Header("ダンパー設定")]

    [SerializeField]
    private float m_baseDamperForce = 20.0f;

    [SerializeField]
    private float m_speedDamperPower = 0.55f;

    [SerializeField]
    private float m_brakeDamperPower = 70.0f;

    [SerializeField, Range(0, 100)]
    private int m_maxDamperForce = 100;

    // ==============================
    // 接続状態
    // ==============================

    private bool m_wasConnected = false;
    private bool m_hasCheckedConnection = false;

    // ==============================
    // デバッグ
    // ==============================

    [Header("デバッグ")]

    [SerializeField, Tooltip("FFBの詳細ログを表示する")]
    private bool m_showDebugLog = false;

    [SerializeField, Tooltip("G923の接続・切断・復帰など重要ログを表示する")]
    private bool m_showConnectionLog = true;

    [SerializeField, Tooltip("Input Systemのデバイス一覧やDeviceChangeログを表示する")]
    private bool m_showInputDeviceLog = false;

    [SerializeField, Tooltip("InputAction値を確認するデバッグログを表示する")]
    private bool m_debugInputActionValues = false;

    private float m_inputDebugTimer = 0.0f;

    private bool m_hasLoggedWaitingForFunction5 = false;

    // ==============================
    // 初期化
    // ==============================

    private void Awake()
    {
        // Time.timeScale = 0 中でもInput Systemが更新されるようにする
        InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;

        if (m_vehicleController == null)
        {
            m_vehicleController = GetComponent<VehicleController>();
        }

        if (m_vehicle == null)
        {
            m_vehicle = gameObject;
        }

        ResetFakeBumpTimer();
    }

    private void OnEnable()
    {
        EnableResumeAction();

        if (m_resumeAction != null && m_resumeAction.action != null)
        {
            m_resumeAction.action.performed += OnResumeActionPerformed;
        }

        InputSystem.onDeviceChange += OnInputDeviceChange;
    }

    private void OnValidate()
    {
        if (m_fakeBumpMagnitudeMin > m_fakeBumpMagnitudeMax)
        {
            m_fakeBumpMagnitudeMin = m_fakeBumpMagnitudeMax;
        }

        if (m_bumpIntervalMin > m_bumpIntervalMax)
        {
            m_bumpIntervalMin = m_bumpIntervalMax;
        }

        if (m_minCrashForce > m_maxCrashForce)
        {
            m_minCrashForce = m_maxCrashForce;
        }
    }

    // ==============================
    // 更新処理
    // ==============================

    private void Update()
    {
        if (!CanCallLogitechSDK())
        {
            return;
        }

        // 再接続後、Function5待ち中なら最優先で復帰入力を見る
        if (m_waitingForFunction5Resume)
        {
            LogitechGSDK.LogiUpdate();

            bool isConnected = LogitechGSDK.LogiIsConnected(m_deviceIndex);

            if (!isConnected)
            {
                UpdateConnectionState(false);
                return;
            }

            EnableResumeAction();

            if (!m_hasLoggedWaitingForFunction5)
            {
                G923Log("[G923 FFB] Function5待ち中です。Function5を押すと復帰します。");
                m_hasLoggedWaitingForFunction5 = true;
            }

            if (WasResumeActionPressedThisFrame())
            {
                G923Log("[G923 FFB] Function5入力を検知しました。復帰処理を実行します。");
                ResumeFromG923Disconnect();
            }

            return;
        }

        if (!m_enableMasterFFB)
        {
            ForceStopAllFFB();
            return;
        }

        if (m_vehicleController == null)
        {
            return;
        }

        if (!LogitechGSDK.LogiUpdate())
        {
            UpdateConnectionState(false);
            return;
        }

        bool isConnectedNormal = LogitechGSDK.LogiIsConnected(m_deviceIndex);

        UpdateConnectionState(isConnectedNormal);

        if (!isConnectedNormal)
        {
            return;
        }

        ApplySteeringOperatingRange();

        if (m_pausedByG923Disconnect && !m_resumeGameOnReconnect)
        {
            ForceStopAllFFB();
            return;
        }

        DebugG923ActionValues();

        float speed = m_vehicleController.KPH;
        float brakeForce = Mathf.Clamp01(m_vehicleController.Brake);

        if (speed < m_lowSpeedThreshold)
        {
            ApplyLowSpeedFFB();
            StopFakeBump();
            UpdateCrashFFB();
            return;
        }

        ApplyDrivingFFB(speed, brakeForce);

        if (m_enableFakeBump)
        {
            UpdateFakeBumps(speed);
        }
        else
        {
            StopFakeBump();
        }

        UpdateCrashFFB();
    }

    // ==============================
    // 実行環境・マスター判定
    // ==============================

    private bool CanCallLogitechSDK()
    {
#if UNITY_EDITOR
        return m_enableFFBInEditor;
#else
        return true;
#endif
    }

    private bool CanUseFFB()
    {
        if (!m_enableMasterFFB)
        {
            return false;
        }

        return CanCallLogitechSDK();
    }

    private void G923Log(string message)
    {
        if (!m_showConnectionLog)
        {
            return;
        }

        AppLog.Log(message);
    }

    private void G923Warning(string message)
    {
        if (!m_showConnectionLog)
        {
            return;
        }

        AppLog.LogWarning(message);
    }

    private void G923VerboseLog(string message)
    {
        if (!m_showDebugLog)
        {
            return;
        }

        AppLog.Log(message);
    }

    private void G923InputDeviceLog(string message)
    {
        if (!m_showInputDeviceLog)
        {
            return;
        }

        AppLog.Log(message);
    }

    // ==============================
    // 接続状態管理 [担当: 中田]
    // ==============================

    private void UpdateConnectionState(bool isConnected)
    {
        if (m_hasCheckedConnection && isConnected == m_wasConnected)
        {
            return;
        }

        m_hasCheckedConnection = true;
        m_wasConnected = isConnected;

        if (isConnected)
        {
            OnG923Connected();
        }
        else
        {
            OnG923Disconnected();
        }
    }

    private void OnG923Connected()
    {
        G923Log("[G923 FFB] G923が接続されました。");

        OnG923ConnectionChanged?.Invoke(true);

        if (m_pausedByG923Disconnect && m_requireFunction5ToResume)
        {
            m_waitingForFunction5Resume = true;
            m_hasLoggedWaitingForFunction5 = false;

            EnableResumeAction();
            ForceStopAllFFB();

            OnG923ResumeStateChanged?.Invoke(EG923ResumeState.WaitingForFunction5);

            G923Warning("[G923 FFB] G923が再接続されました。Function5を押して復帰してください。");
            return;
        }

        if (m_pausedByG923Disconnect && m_resumeGameOnReconnect)
        {
            ResumeFromG923Disconnect();
            return;
        }

        OnG923ResumeStateChanged?.Invoke(EG923ResumeState.Connected);
    }

    private void OnG923Disconnected()
    {
        G923Warning("[G923 FFB] G923の接続が切断されました。FFBを停止します。");

        m_waitingForFunction5Resume = false;
        m_hasLoggedWaitingForFunction5 = false;
        m_steeringOperatingRangeApplied = false;

        ForceStopAllFFB();

        m_isBumping = false;
        ResetFakeBumpTimer();

        OnG923ConnectionChanged?.Invoke(false);
        OnG923ResumeStateChanged?.Invoke(EG923ResumeState.Disconnected);

        if (m_pauseGameOnDisconnect && !m_pausedByG923Disconnect)
        {
            m_timeScaleBeforeG923Pause = Time.timeScale;
            Time.timeScale = 0.0f;
            m_pausedByG923Disconnect = true;

            G923Warning("[G923 FFB] G923切断によりゲームを一時停止しました。");
        }
    }

    // ==============================
    // 復帰処理 [担当: 中田]
    // ==============================

    private void EnableResumeAction()
    {
        if (m_resumeAction == null)
        {
            return;
        }

        if (m_resumeAction.action == null)
        {
            return;
        }

        if (!m_resumeAction.action.enabled)
        {
            m_resumeAction.action.Enable();
        }
    }

    private void DisableResumeAction()
    {
        if (m_resumeAction == null)
        {
            return;
        }

        if (m_resumeAction.action == null)
        {
            return;
        }

        if (m_resumeAction.action.enabled)
        {
            m_resumeAction.action.Disable();
        }
    }

    /// <summary>
    /// 復帰入力として許可するControlか判定する
    /// G923のFunction系入力、またはKeyboardのF5のみ許可する
    /// </summary>
    private bool IsAllowedResumeControl(InputControl control)
    {
        if (control == null)
        {
            return false;
        }

        InputDevice device = control.device;

        if (IsG923Device(device))
        {
            return true;
        }

        if (m_allowKeyboardF5Resume && IsKeyboardF5Control(control))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// G923本体かどうか
    /// </summary>
    private bool IsG923Device(InputDevice device)
    {
        if (device == null)
        {
            return false;
        }

        string displayName = device.displayName ?? string.Empty;
        string deviceName = device.name ?? string.Empty;

        return
            displayName.Contains("G923") ||
            displayName.Contains("Racing Wheel") ||
            deviceName.Contains("G923") ||
            deviceName.Contains("Racing");
    }

    /// <summary>
    /// KeyboardのF5かどうか
    /// </summary>
    private bool IsKeyboardF5Control(InputControl control)
    {
        if (control == null)
        {
            return false;
        }

        if (!(control.device is Keyboard))
        {
            return false;
        }

        return control.path == "/Keyboard/f5";
    }

    private bool WasResumeActionPressedThisFrame()
    {
        if (m_resumeAction == null || m_resumeAction.action == null)
        {
            G923Warning("[G923 FFB] ResumeAction が未設定です。Inspectorを確認してください。");
            return false;
        }

        if (!m_resumeAction.action.enabled)
        {
            m_resumeAction.action.Enable();
        }

        foreach (InputControl control in m_resumeAction.action.controls)
        {
            if (!IsAllowedResumeControl(control))
            {
                continue;
            }

            if (control is ButtonControl buttonControl && buttonControl.wasPressedThisFrame)
            {
                G923Log(
                    "[G923 FFB] 復帰入力を検知しました。control:" +
                    control.path +
                    " device:" +
                    control.device.displayName
                );

                return true;
            }
        }

        return false;
    }

    private bool IsValidFunction5ResumeControl(InputControl control)
    {
        if (control == null)
        {
            return false;
        }

        if (!m_resumeOnlyG923Device)
        {
            return true;
        }

        InputDevice device = control.device;

        if (!IsG923OrJoystickDevice(device))
        {
            return false;
        }

        return true;
    }

    private void OnResumeActionPerformed(InputAction.CallbackContext context)
    {
        if (!context.performed)
        {
            return;
        }

        if (!m_waitingForFunction5Resume)
        {
            G923VerboseLog("[G923 FFB] 復帰入力がありましたが、待機状態ではないため復帰しません。");
            return;
        }

        if (!IsAllowedResumeControl(context.control))
        {
            G923VerboseLog(
                "[G923 FFB] 許可されていないResumeAction入力のため復帰しません。control:" +
                context.control?.path +
                " device:" +
                context.control?.device?.displayName
            );

            return;
        }

        G923Log(
            "[G923 FFB] 復帰入力を検知しました。control:" +
            context.control?.path +
            " device:" +
            context.control?.device?.displayName
        );

        ResumeFromG923Disconnect();
    }

    public void ResumeFromG923Disconnect()
    {
        if (!CanCallLogitechSDK())
        {
            return;
        }

        if (!m_pausedByG923Disconnect && !m_waitingForFunction5Resume)
        {
            return;
        }

        LogitechGSDK.LogiUpdate();

        if (!LogitechGSDK.LogiIsConnected(m_deviceIndex))
        {
            G923Warning("[G923 FFB] G923が未接続のため復帰できません。");
            return;
        }

        if (m_timeScaleBeforeG923Pause <= 0.0f)
        {
            Time.timeScale = 1.0f;
        }
        else
        {
            Time.timeScale = m_timeScaleBeforeG923Pause;
        }

        m_pausedByG923Disconnect = false;
        m_waitingForFunction5Resume = false;
        m_hasLoggedWaitingForFunction5 = false;

        ForceStopAllFFB();
        ResetFakeBumpTimer();

        OnG923ResumeStateChanged?.Invoke(EG923ResumeState.Connected);

        if (m_resumeInputCoroutine != null)
        {
            StopCoroutine(m_resumeInputCoroutine);
        }

        m_resumeInputCoroutine = StartCoroutine(ResumeInputAfterReconnectCoroutine());

        G923Log("[G923 FFB] Function5入力によりゲームを復帰しました。");
    }

    private IEnumerator ResumeInputAfterReconnectCoroutine()
    {
        G923VerboseLog("[G923 FFB] 入力復帰Coroutineを開始します。");

        yield return new WaitForSecondsRealtime(m_inputRefreshDelayAfterReconnect);

        if (m_reinitializeLogitechSDKOnResume)
        {
            ReinitializeLogitechSDKForG923();
        }

        yield return null;

        if (m_refreshInputDevicesOnResume)
        {
            RefreshInputSystemDevicesAfterReconnect();
        }

        yield return null;

        ReactivatePlayerInput();

        G923VerboseLog("[G923 FFB] 入力復帰Coroutineが完了しました。");

        m_resumeInputCoroutine = null;
    }

    private void ReinitializeLogitechSDKForG923()
    {
        G923VerboseLog("[G923 FFB] GameManager経由でLogitech SDKの再初期化を要求します。");

        if (GameManager.Instance == null)
        {
            G923Warning("[G923 FFB] GameManager.Instance が見つからないため、Logitech SDK再初期化を実行できません。");
            return;
        }

        bool result = GameManager.Instance.ForceReinitializeLogitech("G923_FFBController Resume");

        if (result)
        {
            m_steeringOperatingRangeApplied = false;
            ApplySteeringOperatingRange();
        }

        G923VerboseLog("[G923 FFB] GameManager経由 Logitech SDK再初期化結果 : " + result);
    }

    //現在のFFB設定を維持したままG923の操作範囲だけを適用する関数
    private void ApplySteeringOperatingRange()
    {
        if (m_steeringOperatingRangeApplied) { return; }

        LogitechGSDK.LogiControllerPropertiesData properties = new LogitechGSDK.LogiControllerPropertiesData();
        if (!LogitechGSDK.LogiGetCurrentControllerProperties(m_deviceIndex, ref properties)) { return; }

        properties.wheelRange = Mathf.Clamp(m_steeringOperatingRange, 90, 900);
        m_steeringOperatingRangeApplied = LogitechGSDK.LogiSetPreferredControllerProperties(properties);
        if (m_steeringOperatingRangeApplied)
        {
            G923Log("[G923 FFB] 操作範囲を左右合計" + properties.wheelRange + "度へ設定しました。");
        }
    }

    private void ReactivatePlayerInput()
    {
        if (!m_reactivatePlayerInputOnResume)
        {
            G923Warning("[G923 FFB] Reactivate Player Input On Resume がOFFです。");
            return;
        }

        if (m_playerInput == null)
        {
#if UNITY_2023_1_OR_NEWER
            m_playerInput = FindFirstObjectByType<PlayerInput>();
#else
            m_playerInput = FindObjectOfType<PlayerInput>();
#endif
        }

        if (m_inputActionsAsset == null && m_playerInput != null)
        {
            m_inputActionsAsset = m_playerInput.actions;
        }

        if (m_inputActionsAsset == null)
        {
            G923Warning("[G923 FFB] InputActionAssetが見つかりません。Inspectorの Input Actions Asset に Aim2024Input を入れてください。");
        }
        else
        {
            m_inputActionsAsset.Disable();
            InputSystem.Update();

            m_inputActionsAsset.Enable();
            InputSystem.Update();

            InputActionMap systemMap = m_inputActionsAsset.FindActionMap(m_systemActionMapName, false);

            if (systemMap != null)
            {
                systemMap.Enable();
                G923VerboseLog("[G923 FFB] ActionMapを有効化 : " + m_systemActionMapName);
            }
            else
            {
                G923Warning("[G923 FFB] System ActionMapが見つかりません : " + m_systemActionMapName);
            }

            InputActionMap gameplayMap = m_inputActionsAsset.FindActionMap(m_gameplayActionMapName, false);

            if (gameplayMap != null)
            {
                gameplayMap.Enable();
                G923VerboseLog("[G923 FFB] ActionMapを有効化 : " + m_gameplayActionMapName);
            }
            else
            {
                G923Warning("[G923 FFB] Gameplay ActionMapが見つかりません : " + m_gameplayActionMapName);
            }
        }

        if (m_playerInput != null)
        {
            m_playerInput.DeactivateInput();
            InputSystem.Update();

            m_playerInput.ActivateInput();
            InputSystem.Update();

            if (m_switchToGameplayActionMapOnResume)
            {
                try
                {
                    m_playerInput.SwitchCurrentActionMap(m_gameplayActionMapName);
                    G923VerboseLog("[G923 FFB] PlayerInputのActionMapを " + m_gameplayActionMapName + " に切り替えました。");
                }
                catch
                {
                    G923Warning("[G923 FFB] PlayerInputのActionMap切り替えに失敗しました : " + m_gameplayActionMapName);
                }
            }

            if (m_playerInput.currentActionMap != null)
            {
                G923VerboseLog("[G923 FFB] 現在のPlayerInput ActionMap : " + m_playerInput.currentActionMap.name);
            }

            RePairCurrentG923ToPlayerInput();
        }
        else
        {
            G923Warning("[G923 FFB] PlayerInputが見つかりませんでした。Project-wide Actionsのみ再有効化しました。");
        }

        EnableResumeAction();

        if (m_showDebugLog && m_inputActionsAsset != null)
        {
            G923VerboseLog("[G923 FFB] InputActionAsset : " + m_inputActionsAsset.name);

            foreach (InputActionMap map in m_inputActionsAsset.actionMaps)
            {
                G923VerboseLog("[G923 FFB] ActionMap状態 : " + map.name + " / enabled = " + map.enabled);

                foreach (InputAction action in map.actions)
                {
                    G923VerboseLog("[G923 FFB] Action状態 : " + map.name + "/" + action.name + " / enabled = " + action.enabled);
                }
            }
        }

        G923Log("[G923 FFB] 入力復帰処理が完了しました。");
    }

    private void DebugG923ActionValues()
    {
        if (!m_debugInputActionValues)
        {
            return;
        }

        if (m_inputActionsAsset == null)
        {
            return;
        }

        m_inputDebugTimer -= Time.unscaledDeltaTime;

        if (m_inputDebugTimer > 0.0f)
        {
            return;
        }

        m_inputDebugTimer = 1.0f;

        InputActionMap inGameMap = m_inputActionsAsset.FindActionMap("InGame", false);

        if (inGameMap == null)
        {
            AppLog.LogWarning("[G923 Debug] InGame ActionMapが見つかりません。");
            return;
        }

        DebugActionValue(inGameMap, "Handle");
        DebugActionValue(inGameMap, "Accel");
        DebugActionValue(inGameMap, "AccelPedal");
        DebugActionValue(inGameMap, "Brake");
        DebugActionValue(inGameMap, "BrakePedal");
    }

    private void DebugActionValue(InputActionMap map, string actionName)
    {
        InputAction action = map.FindAction(actionName, false);

        if (action == null)
        {
            AppLog.LogWarning("[G923 Debug] Actionが見つかりません : " + actionName);
            return;
        }

        float value = 0.0f;

        try
        {
            value = action.ReadValue<float>();
        }
        catch
        {
            AppLog.LogWarning("[G923 Debug] floatで読めないActionです : " + actionName);
            return;
        }

        AppLog.Log(
            "[G923 Debug] " +
            actionName +
            " / enabled:" + action.enabled +
            " / value:" + value +
            " / controls:" + action.controls.Count
        );

        for (int i = 0; i < action.controls.Count; i++)
        {
            InputControl control = action.controls[i];

            AppLog.Log(
                "[G923 Debug]   control:" +
                control.path +
                " / device:" +
                control.device.displayName +
                " / deviceId:" +
                control.device.deviceId
            );
        }
    }

    // ==============================
    // Input System デバイス管理 [担当: 中田]
    // ==============================

    private void OnInputDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (!IsG923OrJoystickDevice(device))
        {
            return;
        }

        G923InputDeviceLog(
            "[G923 InputSystem] DeviceChange : " +
            change +
            " / displayName:" + device.displayName +
            " / name:" + device.name +
            " / layout:" + device.layout +
            " / id:" + device.deviceId
        );
    }

    private bool IsG923OrJoystickDevice(InputDevice device)
    {
        if (device == null)
        {
            return false;
        }

        string displayName = device.displayName ?? string.Empty;
        string deviceName = device.name ?? string.Empty;
        string layoutName = device.layout ?? string.Empty;

        string deviceText = (displayName + " " + deviceName + " " + layoutName).ToLowerInvariant();

        return
            deviceText.Contains("g923") ||
            deviceText.Contains("racing wheel") ||
            deviceText.Contains("logitech") ||
            deviceText.Contains("logicool");
    }

    /// <summary>
    /// USB再接続後にInput System側のJoystick / G923デバイスを再同期する
    /// </summary>
    private void RefreshInputSystemDevicesAfterReconnect()
    {
        if (!m_refreshInputDevicesOnResume)
        {
            return;
        }

        G923Log("[G923 FFB] Input Systemデバイスの再同期を開始します。");

        InputSystem.Update();

        bool foundTargetDevice = false;

        foreach (InputDevice device in InputSystem.devices)
        {
            G923InputDeviceLog(
                "[G923 InputSystem] Device一覧 : " +
                device.displayName +
                " / name:" + device.name +
                " / layout:" + device.layout +
                " / id:" + device.deviceId +
                " / enabled:" + device.enabled
            );

            if (!IsG923OrJoystickDevice(device))
            {
                continue;
            }

            foundTargetDevice = true;

            G923Log(
                "[G923 FFB] InputDevice再同期対象 : " +
                device.displayName +
                " / name:" + device.name +
                " / layout:" + device.layout +
                " / id:" + device.deviceId
            );

            if (!device.enabled)
            {
                InputSystem.EnableDevice(device);
                G923Log("[G923 FFB] InputDeviceをEnableしました : " + device.displayName);
            }

            if (m_resetInputSystemDevicesOnResume)
            {
                InputSystem.ResetDevice(device, true);
                G923InputDeviceLog("[G923 FFB] InputDeviceをResetしました : " + device.displayName);
            }
        }

        InputSystem.Update();

        if (!foundTargetDevice)
        {
            G923Warning("[G923 FFB] 再同期対象のG923デバイスが見つかりませんでした。");
        }

        G923Log("[G923 FFB] Input Systemデバイスの再同期が完了しました。");
    }

    /// <summary>
    /// 再接続後のG923をPlayerInputに明示的にペアリングし直す
    /// </summary>
    private void RePairCurrentG923ToPlayerInput()
    {
        if (m_playerInput == null)
        {
            G923Warning("[G923 FFB] PlayerInputがないため、G923再ペアリングをスキップします。");
            return;
        }

        InputDevice targetDevice = null;

        foreach (InputDevice device in InputSystem.devices)
        {
            if (!IsG923OrJoystickDevice(device))
            {
                continue;
            }

            targetDevice = device;
            break;
        }

        if (targetDevice == null)
        {
            G923Warning("[G923 FFB] PlayerInputへ再ペアリングするG923が見つかりません。");
            return;
        }

        G923Log(
            "[G923 FFB] PlayerInputへG923を再ペアリングします : " +
            targetDevice.displayName +
            " / id:" +
            targetDevice.deviceId
        );

        try
        {
            m_playerInput.user.UnpairDevices();

            InputUser.PerformPairingWithDevice(targetDevice, m_playerInput.user);

            if (Keyboard.current != null)
            {
                InputUser.PerformPairingWithDevice(Keyboard.current, m_playerInput.user);
            }

            m_playerInput.ActivateInput();

            if (m_switchToGameplayActionMapOnResume)
            {
                m_playerInput.SwitchCurrentActionMap(m_gameplayActionMapName);
            }

            G923Log("[G923 FFB] PlayerInputのG923再ペアリングが完了しました。");
        }
        catch (Exception e)
        {
            AppLog.LogError("[G923 FFB] PlayerInputのG923再ペアリング中に例外 : " + e.Message);
        }
    }

    // ==============================
    // 通常FFB反映 [担当: 中田]
    // ==============================

    private void ApplyLowSpeedFFB()
    {
        if (m_crashActiveTimer <= 0.0f)
        {
            LogitechGSDK.LogiStopConstantForce(m_deviceIndex);
        }

        int saturation = Mathf.Clamp(Mathf.RoundToInt(m_lowSpeedSpringSaturation * m_masterGain), 0, 100);
        int coefficient = Mathf.Clamp(Mathf.RoundToInt(m_lowSpeedSpringCoefficient * m_masterGain), 0, 100);
        int damper = Mathf.Clamp(Mathf.RoundToInt(m_lowSpeedDamperForce * m_masterGain), 0, 100);

        LogitechGSDK.LogiPlaySpringForce(m_deviceIndex, 0, saturation, coefficient);
        LogitechGSDK.LogiPlayDamperForce(m_deviceIndex, damper);

        G923VerboseLog($"[G923 FFB] LowSpeed | Sat:{saturation} Coef:{coefficient} Damper:{damper}");
    }

    private void ApplyDrivingFFB(float speed, float brakeForce)
    {
        if (m_crashActiveTimer <= 0.0f)
        {
            LogitechGSDK.LogiStopConstantForce(m_deviceIndex);
        }

        float speedRate = Mathf.Clamp01(speed / m_springSpeedReference);

        int springSaturation = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(m_minSpringSaturation, m_maxSpringSaturation, speedRate)),
            0,
            100
        );

        int springCoefficient = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(m_minSpringCoefficient, m_maxSpringCoefficient, speedRate)),
            0,
            100
        );

        springSaturation = Mathf.Clamp(Mathf.RoundToInt(springSaturation * m_masterGain), 0, 100);
        springCoefficient = Mathf.Clamp(Mathf.RoundToInt(springCoefficient * m_masterGain), 0, 100);

        LogitechGSDK.LogiPlaySpringForce(m_deviceIndex, 0, springSaturation, springCoefficient);

        int damperForce = Mathf.Clamp(
            Mathf.RoundToInt(m_baseDamperForce + (speed * m_speedDamperPower) + (brakeForce * m_brakeDamperPower)),
            0,
            m_maxDamperForce
        );

        damperForce = Mathf.Clamp(Mathf.RoundToInt(damperForce * m_masterGain), 0, 100);

        LogitechGSDK.LogiPlayDamperForce(m_deviceIndex, damperForce);

        G923VerboseLog($"[G923 FFB] Driving | Speed:{speed:F1} Sat:{springSaturation} Coef:{springCoefficient} Damper:{damperForce}");
    }

    // ==============================
    // 仮の路面凹凸 [担当: 中里]
    // ==============================

    private void UpdateFakeBumps(float speed)
    {
        float speedRatioForInterval = speed / 130.0f;
        float speedMultiplier = Mathf.Pow(speedRatioForInterval, 2.0f);
        speedMultiplier = Mathf.Max(0.1f, speedMultiplier);

        if (m_isBumping)
        {
            m_bumpActiveTimer -= Time.deltaTime;

            if (m_bumpActiveTimer <= 0.0f)
            {
                StopFakeBump();
                m_bumpWaitTimer = UnityEngine.Random.Range(m_bumpIntervalMin, m_bumpIntervalMax);
            }
        }
        else
        {
            m_bumpWaitTimer -= Time.deltaTime * speedMultiplier;

            if (m_bumpWaitTimer <= 0.0f)
            {
                float magnitudeFactor = Mathf.Clamp(speed / 100.0f, 0.2f, 1.5f);
                int baseMagnitude = UnityEngine.Random.Range(m_fakeBumpMagnitudeMin, m_fakeBumpMagnitudeMax + 1);

                int finalMagnitude = Mathf.Clamp(
                    Mathf.RoundToInt(baseMagnitude * magnitudeFactor * m_masterGain),
                    1,
                    100
                );

                int effectType = UnityEngine.Random.Range(0, 3);

                switch (effectType)
                {
                    case 0:
                        LogitechGSDK.LogiPlayBumpyRoadEffect(m_deviceIndex, finalMagnitude);
                        break;

                    case 1:
                        LogitechGSDK.LogiPlayDirtRoadEffect(m_deviceIndex, finalMagnitude);
                        break;

                    case 2:
                        int period = Mathf.RoundToInt(Mathf.Lerp(120, 40, Mathf.Clamp01(speed / 100.0f)));
                        LogitechGSDK.LogiPlaySurfaceEffect(m_deviceIndex, 1, finalMagnitude, period);
                        break;
                }

                m_isBumping = true;

                float durationFactor = Mathf.Clamp(1.2f - (speed / 150.0f), 0.5f, 1.5f);
                m_bumpActiveTimer = m_bumpDuration * UnityEngine.Random.Range(0.8f, 1.2f) * durationFactor;

                G923VerboseLog($"[G923 FFB] FakeBump | Mag:{finalMagnitude}");
            }
        }
    }

    private void StopFakeBump()
    {
        if (!CanCallLogitechSDK())
        {
            return;
        }

        if (!m_isBumping)
        {
            return;
        }

        LogitechGSDK.LogiStopBumpyRoadEffect(m_deviceIndex);
        LogitechGSDK.LogiStopDirtRoadEffect(m_deviceIndex);
        LogitechGSDK.LogiStopSurfaceEffect(m_deviceIndex);

        m_isBumping = false;
    }

    private void ResetFakeBumpTimer()
    {
        m_bumpWaitTimer = UnityEngine.Random.Range(m_bumpIntervalMin, m_bumpIntervalMax);
        m_bumpActiveTimer = 0.0f;
    }

    // ==============================
    // 物理衝突FFB [担当: 中里]
    // ==============================

    private void OnCollisionEnter(Collision collision)
    {
        if (!CanUseFFB())
        {
            return;
        }

        if (!m_enableCrashFFB)
        {
            return;
        }

        if (!LogitechGSDK.LogiIsConnected(m_deviceIndex))
        {
            return;
        }

        if (collision.contactCount <= 0)
        {
            return;
        }

        float impactSpeed = collision.relativeVelocity.magnitude;

        if (impactSpeed < m_minCrashImpactSpeed)
        {
            return;
        }

        float impactIntensity = Mathf.Clamp01(impactSpeed / 8.0f);

        Vector3 contactPoint = collision.GetContact(0).point;
        Vector3 localContact = transform.InverseTransformPoint(contactPoint);

        bool isHitRightSide = localContact.x > 0.0f;

        PlayWallCrashFFB(isHitRightSide, impactIntensity);
    }

    public void PlayWallCrashFFB(bool isHitRightSide, float impactIntensity)
    {
        if (!CanUseFFB())
        {
            return;
        }

        m_crashActiveTimer = m_crashDuration;

        int baseDirection = isHitRightSide ? -1 : 1;

        if (m_invertCrashForce)
        {
            baseDirection *= -1;
        }

        int safeMinCrashForce = Mathf.Min(m_minCrashForce, m_maxCrashForce);

        int forceMagnitude = Mathf.Clamp(
            Mathf.RoundToInt(m_maxCrashForce * Mathf.Clamp01(impactIntensity)),
            safeMinCrashForce,
            m_maxCrashForce
        );

        m_currentCrashForce = forceMagnitude * baseDirection;

        G923VerboseLog($"[G923 FFB] Wall Crash | Force:{m_currentCrashForce}");
    }

    private void UpdateCrashFFB()
    {
        if (m_crashActiveTimer <= 0.0f)
        {
            return;
        }

        m_crashActiveTimer -= Time.deltaTime;

        if (m_crashActiveTimer > 0.0f)
        {
            float remainRate = Mathf.Clamp01(m_crashActiveTimer / Mathf.Max(0.01f, m_crashDuration));

            int outputForce = Mathf.RoundToInt(m_currentCrashForce * remainRate * m_masterGain);
            outputForce = Mathf.Clamp(outputForce, -100, 100);

            LogitechGSDK.LogiPlayConstantForce(m_deviceIndex, outputForce);
        }
        else
        {
            m_crashActiveTimer = 0.0f;
            m_currentCrashForce = 0;
            LogitechGSDK.LogiStopConstantForce(m_deviceIndex);
        }
    }

    // ==============================
    // 安全停止
    // ==============================

    public void StopAllFFB()
    {
        if (!CanCallLogitechSDK())
        {
            return;
        }

        ForceStopAllFFB();
    }

    private void ForceStopAllFFB()
    {
        if (!CanCallLogitechSDK())
        {
            return;
        }

        LogitechGSDK.LogiStopConstantForce(m_deviceIndex);
        LogitechGSDK.LogiStopSpringForce(m_deviceIndex);
        LogitechGSDK.LogiStopDamperForce(m_deviceIndex);

        LogitechGSDK.LogiStopBumpyRoadEffect(m_deviceIndex);
        LogitechGSDK.LogiStopDirtRoadEffect(m_deviceIndex);
        LogitechGSDK.LogiStopSlipperyRoadEffect(m_deviceIndex);
        LogitechGSDK.LogiStopSurfaceEffect(m_deviceIndex);

        LogitechGSDK.LogiStopCarAirborne(m_deviceIndex);
        LogitechGSDK.LogiStopSoftstopForce(m_deviceIndex);

        m_isBumping = false;
        m_crashActiveTimer = 0.0f;
        m_currentCrashForce = 0;

        ResetFakeBumpTimer();

        G923VerboseLog("[G923 FFB] ForceStopAllFFB");
    }

    private void OnDisable()
    {
        if (m_resumeAction != null && m_resumeAction.action != null)
        {
            m_resumeAction.action.performed -= OnResumeActionPerformed;
        }

        InputSystem.onDeviceChange -= OnInputDeviceChange;

        if (m_resumeInputCoroutine != null)
        {
            StopCoroutine(m_resumeInputCoroutine);
            m_resumeInputCoroutine = null;
        }

        StopAllFFB();
        DisableResumeAction();
    }

    private void OnDestroy()
    {
        StopAllFFB();
    }
}
