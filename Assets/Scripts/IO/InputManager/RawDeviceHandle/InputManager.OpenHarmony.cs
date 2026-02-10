#if UNITY_OPENHARMONY
using AOT;
using MajdataPlay.Collections;
using MajdataPlay.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
#nullable enable
namespace MajdataPlay.IO
{
    internal static partial class InputManager
    {
        static class OHInput
        {
            public static bool IsInitialized { get; private set; } = false;
            public static bool IsInterceptorRegistered { get; private set; } = false;
            
            // SpinLock for thread-safe state synchronization (matching TouchPanel pattern)
            private static SpinLock _syncLock = new();
            
            // Sensor state arrays (35 zones: A1-A8, B1-B8, C1-C2, D1-D8, E1-E8 + 1 extra)
            private static readonly bool[] _sensorStates = new bool[35];
            private static readonly bool[] _sensorRealTimeStates = new bool[35];
            private static readonly bool[] _isSensorHadOn = new bool[35];
            private static readonly bool[] _isSensorHadOff = new bool[35];
            private static readonly bool[] _isSensorHadOnInternal = new bool[35];
            private static readonly bool[] _isSensorHadOffInternal = new bool[35];
            
            // Click counting per finger (for sensorClickedCount)
            private static readonly ConcurrentDictionary<int, ulong> _fingerZoneStates = new();
            private static readonly int[] _sensorClickedCountInternal = new int[35];
            private static readonly int[] _sensorClickedCount = new int[35];
            
            // Extra button states (for outer button ring)
            private static readonly bool[] _extraButtonStates = new bool[12];
            private static readonly bool[] _extraButtonStatesInternal = new bool[12];
            
            // Screen dimensions for coordinate conversion (updated atomically)
            private static volatile int _screenWidth = 1920;
            private static volatile int _screenHeight = 1080;
            private static volatile float _worldScale = 1f;
            private static volatile float _worldOffsetX = 0f;
            private static volatile float _worldOffsetY = 0f;
            
            // Zone geometry constants (from NoteHelper.GetTouchAreaDistance)
            // Ring radii in world units
            private const float RADIUS_A = 4.0f;
            private const float RADIUS_B = 2.2f;
            private const float RADIUS_D = 4.1f;
            private const float RADIUS_E = 3.1f;
            private const float RADIUS_C = 0.8f; // Center zone radius
            
            // Ring boundary radii (midpoints between rings)
            private const float RING_BOUNDARY_OUTER = 5.4f;      // Beyond this is button area
            private const float RING_BOUNDARY_A_D = 4.05f;       // Between A and D
            private const float RING_BOUNDARY_D_E = 3.6f;        // Between D and E
            private const float RING_BOUNDARY_E_B = 2.65f;       // Between E and B
            private const float RING_BOUNDARY_B_C = 1.1f;        // Between B and C
            
            // Angle offset constants
            // A/B use: angle = -index * (π/4) + π * 5/8
            // D/E use: angle = -index * (π/4) + π * 6/8
            private const float ANGLE_OFFSET_AB = Mathf.PI * 5f / 8f;
            private const float ANGLE_OFFSET_DE = Mathf.PI * 6f / 8f;
            
            private static OHInputInterop.Input_TouchEventCallback _touchDelegate = null!;
            private static OHInputInterop.Input_MouseEventCallback _mouseDelegate = null!;
            private static OHInputInterop.Input_AxisEventCallback _axisDelegate = null!;
            private static OHInputInterop.Input_InterceptorEventCallback _eventCallback;
            
            public static void Init()
            {
                if (IsInitialized)
                {
                    MajDebug.LogWarning("[OHInput] Already initialized");
                    return;
                }
                try
                {
                    // Initialize screen dimensions
                    _screenWidth = Screen.width;
                    _screenHeight = Screen.height;
                    UpdateWorldTransform();
                    
                    RegisterInterceptor();
                    IsInitialized = true;
                    MajDebug.LogInfo($"[OHInput] Initialized successfully. Screen: {_screenWidth}x{_screenHeight}, WorldScale: {_worldScale}");
                }
                catch (Exception e)
                {
                    MajDebug.LogException(e);
                    MajDebug.LogError("[OHInput] Failed to initialize");
                }
            }

            public static void Shutdown()
            {
                if (!IsInitialized)
                {
                    return;
                }
                try
                {
                    UnregisterInterceptor();
                    _fingerZoneStates.Clear();
                    Array.Clear(_sensorStates, 0, _sensorStates.Length);
                    Array.Clear(_sensorRealTimeStates, 0, _sensorRealTimeStates.Length);
                    Array.Clear(_isSensorHadOn, 0, _isSensorHadOn.Length);
                    Array.Clear(_isSensorHadOff, 0, _isSensorHadOff.Length);
                    Array.Clear(_isSensorHadOnInternal, 0, _isSensorHadOnInternal.Length);
                    Array.Clear(_isSensorHadOffInternal, 0, _isSensorHadOffInternal.Length);
                    Array.Clear(_sensorClickedCountInternal, 0, _sensorClickedCountInternal.Length);
                    Array.Clear(_sensorClickedCount, 0, _sensorClickedCount.Length);
                    Array.Clear(_extraButtonStates, 0, _extraButtonStates.Length);
                    Array.Clear(_extraButtonStatesInternal, 0, _extraButtonStatesInternal.Length);
                    IsInitialized = false;
                    MajDebug.LogInfo("[OHInput] Shutdown successfully");
                }
                catch (Exception e)
                {
                    MajDebug.LogException(e);
                }
            }
            
            /// <summary>
            /// Update world transform parameters based on camera settings.
            /// Call this when screen size changes.
            /// </summary>
            public static void UpdateWorldTransform()
            {
                // Calculate scale factor from screen to world coordinates
                // Assuming orthographic camera with standard settings
                // The touch panel is centered at (0,0,0) in world space
                // Screen center maps to world (0,0)
                var screenMin = Mathf.Min(_screenWidth, _screenHeight);
                // The outer button boundary is at radius 5.4 in world units
                // We want to map the inscribed circle of the screen to this
                _worldScale = (RING_BOUNDARY_OUTER * 2.2f) / screenMin;
                _worldOffsetX = _screenWidth / 2f;
                _worldOffsetY = _screenHeight / 2f;
            }

            private static void RegisterInterceptor()
            {
                if (IsInterceptorRegistered)
                {
                    MajDebug.LogWarning("[OHInput] Interceptor already registered");
                    return;
                }
                _touchDelegate = OnNativeTouchEvent;
                _mouseDelegate = OnNativeMouseEvent;
                _axisDelegate = OnNativeAxisEvent;
                _eventCallback = new OHInputInterop.Input_InterceptorEventCallback
                {
                    mouseCallback = Marshal.GetFunctionPointerForDelegate(_mouseDelegate),
                    touchCallback = Marshal.GetFunctionPointerForDelegate(_touchDelegate),
                    axisCallback = Marshal.GetFunctionPointerForDelegate(_axisDelegate)
                };
                int result = OHInputInterop.OH_Input_AddInputEventInterceptor(
                    ref _eventCallback,
                    IntPtr.Zero
                );
                var inputResult = (OHInputInterop.InputResult)result;
                switch (inputResult)
                {
                    case OHInputInterop.InputResult.Success:
                        IsInterceptorRegistered = true;
                        MajDebug.LogInfo("[OHInput] Interceptor registered successfully");
                        break;
                    case OHInputInterop.InputResult.PermissionDenied:
                        MajDebug.LogError("[OHInput] Permission denied - check module.json5 permissions");
                        break;
                    case OHInputInterop.InputResult.RepeatInterceptor:
                        IsInterceptorRegistered = true;
                        MajDebug.LogWarning("[OHInput] Interceptor already exists (repeat registration)");
                        break;
                    default:
                        MajDebug.LogError($"[OHInput] Failed to register interceptor: {inputResult} ({result})");
                        break;
                }
            }

            private static void UnregisterInterceptor()
            {
                if (!IsInterceptorRegistered)
                {
                    return;
                }
                int result = OHInputInterop.OH_Input_RemoveInputEventInterceptor();
                IsInterceptorRegistered = false;
                MajDebug.LogInfo($"[OHInput] Interceptor unregistered (result: {result})");
            }

            [MonoPInvokeCallback(typeof(OHInputInterop.Input_MouseEventCallback))]
            private static void OnNativeMouseEvent(IntPtr mouseEvent)
            {
                // Not used
            }

            [MonoPInvokeCallback(typeof(OHInputInterop.Input_AxisEventCallback))]
            private static void OnNativeAxisEvent(IntPtr axisEvent)
            {
                // Not used - no axis input handling needed
            }

            /// <summary>
            /// Native touch event callback - runs on native thread.
            /// Converts coordinates to zones immediately and updates state arrays.
            /// </summary>
            [MonoPInvokeCallback(typeof(OHInputInterop.Input_TouchEventCallback))]
            private static void OnNativeTouchEvent(IntPtr touchEvent)
            {
                try
                {
                    var action = OHInputInterop.OH_Input_GetTouchEventAction(touchEvent);
                    var fingerId = OHInputInterop.OH_Input_GetTouchEventFingerId(touchEvent);
                    var displayX = OHInputInterop.OH_Input_GetTouchEventDisplayX(touchEvent);
                    var displayY = OHInputInterop.OH_Input_GetTouchEventDisplayY(touchEvent);
                    
                    var touchAction = (OHInputInterop.TouchAction)(action & 0xFF);
                    
                    // Convert OH coordinates (top-left origin) to Unity coordinates (bottom-left origin)
                    var screenHeight = _screenHeight;
                    var unityY = screenHeight - displayY;
                    
                    // Convert screen coordinates to zones using pure math
                    var newZoneState = CoordinateToZoneState(displayX, unityY, out var extraButton);
                    
                    switch (touchAction)
                    {
                        case OHInputInterop.TouchAction.Down:
                        case OHInputInterop.TouchAction.Move:
                            {
                                // Get previous zone state for this finger
                                _fingerZoneStates.TryGetValue(fingerId, out var prevZoneState);
                                
                                // Update finger's zone state
                                _fingerZoneStates[fingerId] = newZoneState;
                                
                                // Update state arrays with SpinLock protection
                                var isLocked = false;
                                try
                                {
                                    _syncLock.Enter(ref isLocked);
                                    
                                    // Update sensor states and accumulate hadOn
                                    for (var i = 0; i < 34; i++)
                                    {
                                        var prevState = (prevZoneState & (1UL << i)) != 0;
                                        var currState = (newZoneState & (1UL << i)) != 0;
                                        
                                        if (currState)
                                        {
                                            _sensorRealTimeStates[i] = true;
                                            _isSensorHadOnInternal[i] = true;
                                        }
                                        
                                        // Count transitions from off to on (click detection)
                                        if (!prevState && currState)
                                        {
                                            _sensorClickedCountInternal[i]++;
                                        }
                                    }
                                    
                                    // Update extra button state
                                    if (extraButton >= 0 && extraButton < 12)
                                    {
                                        _extraButtonStatesInternal[extraButton] = true;
                                    }
                                }
                                finally
                                {
                                    if (isLocked)
                                    {
                                        _syncLock.Exit();
                                    }
                                }
                            }
                            break;
                            
                        case OHInputInterop.TouchAction.Up:
                        case OHInputInterop.TouchAction.Cancel:
                            {
                                // Get previous zone state before removal to mark hadOff
                                if (_fingerZoneStates.TryRemove(fingerId, out var prevZoneState))
                                {
                                    var isLocked = false;
                                    try
                                    {
                                        _syncLock.Enter(ref isLocked);
                                        
                                        // Mark zones that were on as hadOff
                                        for (var i = 0; i < 34; i++)
                                        {
                                            if ((prevZoneState & (1UL << i)) != 0)
                                            {
                                                _isSensorHadOffInternal[i] = true;
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        if (isLocked)
                                        {
                                            _syncLock.Exit();
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
                catch (Exception e)
                {
                    // Cannot use MajDebug here as it may not be thread-safe
                    UnityEngine.Debug.LogException(e);
                }
            }
            
            /// <summary>
            /// Convert screen coordinates to zone state bitmask using pure polar coordinate math.
            /// This runs on the native callback thread without requiring Unity main thread.
            /// </summary>
            /// <param name="screenX">Screen X coordinate</param>
            /// <param name="screenY">Screen Y coordinate (Unity convention, bottom-left origin)</param>
            /// <param name="extraButton">Output: extra button index (0-7 for buttons, 8+ for other areas, -1 if none)</param>
            /// <returns>Bitmask of active zones (bits 0-33 for sensors)</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ulong CoordinateToZoneState(float screenX, float screenY, out int extraButton)
            {
                extraButton = -1;
                
                // Convert screen coordinates to world coordinates
                var worldX = (screenX - _worldOffsetX) * _worldScale;
                var worldY = (screenY - _worldOffsetY) * _worldScale;
                
                // Calculate polar coordinates
                var radius = Mathf.Sqrt(worldX * worldX + worldY * worldY);
                var angle = Mathf.Atan2(worldY, worldX); // Range: -π to π
                
                // Check if outside play area (button ring)
                if (radius > RING_BOUNDARY_OUTER)
                {
                    // Extra button area
                    extraButton = 9; // Generic "outside" button
                    return 0UL;
                }
                
                // Check for button ring area (between outer sensors and button boundary)
                if (radius > RING_BOUNDARY_OUTER * 0.85f)
                {
                    // Calculate button index from angle
                    // Buttons are arranged: A1-A8 clockwise from top
                    var degree = -angle * Mathf.Rad2Deg + 180f;
                    if (degree < 0) degree += 360f;
                    if (degree >= 360f) degree -= 360f;
                    var pos = (int)(degree / 45f);
                    switch (pos)
                    {
                        case 0: extraButton = 6; break;
                        case 1: extraButton = 7; break;
                        default: extraButton = (pos - 2); break;
                    }
                    return 0UL;
                }
                
                ulong zoneState = 0UL;
                
                // Determine which ring(s) the touch falls into
                // Using finger radius simulation - check multiple zones
                var fingerRadius = FingerRadius * _worldScale;
                
                // Check each ring based on radius
                if (radius < RING_BOUNDARY_B_C)
                {
                    // Center zone (C1/C2)
                    // C1 and C2 are the two halves of the center
                    // Based on angle, determine which half
                    if (angle >= 0)
                    {
                        zoneState |= 1UL << 16; // C1
                    }
                    else
                    {
                        zoneState |= 1UL << 17; // C2
                    }
                    // Also check B ring if close to boundary
                    if (radius > RING_BOUNDARY_B_C - fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.B, fingerRadius);
                    }
                }
                else if (radius < RING_BOUNDARY_E_B)
                {
                    // B ring
                    zoneState |= GetRingZoneState(radius, angle, SensorRing.B, fingerRadius);
                    // Check adjacent rings
                    if (radius < RING_BOUNDARY_B_C + fingerRadius)
                    {
                        // Close to center
                        if (angle >= 0)
                            zoneState |= 1UL << 16;
                        else
                            zoneState |= 1UL << 17;
                    }
                    if (radius > RING_BOUNDARY_E_B - fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.E, fingerRadius);
                    }
                }
                else if (radius < RING_BOUNDARY_D_E)
                {
                    // E ring
                    zoneState |= GetRingZoneState(radius, angle, SensorRing.E, fingerRadius);
                    // Check adjacent rings
                    if (radius < RING_BOUNDARY_E_B + fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.B, fingerRadius);
                    }
                    if (radius > RING_BOUNDARY_D_E - fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.D, fingerRadius);
                    }
                }
                else if (radius < RING_BOUNDARY_A_D)
                {
                    // D ring
                    zoneState |= GetRingZoneState(radius, angle, SensorRing.D, fingerRadius);
                    // Check adjacent rings
                    if (radius < RING_BOUNDARY_D_E + fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.E, fingerRadius);
                    }
                    if (radius > RING_BOUNDARY_A_D - fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.A, fingerRadius);
                    }
                }
                else
                {
                    // A ring (outermost sensor ring)
                    zoneState |= GetRingZoneState(radius, angle, SensorRing.A, fingerRadius);
                    // Check D ring if close
                    if (radius < RING_BOUNDARY_A_D + fingerRadius)
                    {
                        zoneState |= GetRingZoneState(radius, angle, SensorRing.D, fingerRadius);
                    }
                    
                    // Check for extra button overlap
                    if (radius > RING_BOUNDARY_OUTER * 0.85f - fingerRadius)
                    {
                        var degree = -angle * Mathf.Rad2Deg + 180f;
                        if (degree < 0) degree += 360f;
                        if (degree >= 360f) degree -= 360f;
                        var pos = (int)(degree / 45f);
                        switch (pos)
                        {
                            case 0: extraButton = 6; break;
                            case 1: extraButton = 7; break;
                            default: extraButton = (pos - 2); break;
                        }
                    }
                }
                
                return zoneState;
            }
            
            private enum SensorRing { A, B, D, E }
            
            /// <summary>
            /// Get zone state bitmask for a specific ring based on angle.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ulong GetRingZoneState(float radius, float angle, SensorRing ring, float fingerRadius)
            {
                ulong state = 0UL;
                
                // Calculate sector index from angle
                // A/B rings use ANGLE_OFFSET_AB, D/E rings use ANGLE_OFFSET_DE
                float angleOffset;
                int baseIndex;
                
                switch (ring)
                {
                    case SensorRing.A:
                        angleOffset = ANGLE_OFFSET_AB;
                        baseIndex = 0; // A1-A8 = indices 0-7
                        break;
                    case SensorRing.B:
                        angleOffset = ANGLE_OFFSET_AB;
                        baseIndex = 8; // B1-B8 = indices 8-15
                        break;
                    case SensorRing.D:
                        angleOffset = ANGLE_OFFSET_DE;
                        baseIndex = 18; // D1-D8 = indices 18-25
                        break;
                    case SensorRing.E:
                        angleOffset = ANGLE_OFFSET_DE;
                        baseIndex = 26; // E1-E8 = indices 26-33
                        break;
                    default:
                        return 0UL;
                }
                
                // Convert angle to sector index
                // The formula from NoteHelper: angle = -index * (π/4) + angleOffset
                // Solving for index: index = (angleOffset - angle) / (π/4)
                var normalizedAngle = angleOffset - angle;
                // Normalize to [0, 2π)
                while (normalizedAngle < 0) normalizedAngle += Mathf.PI * 2f;
                while (normalizedAngle >= Mathf.PI * 2f) normalizedAngle -= Mathf.PI * 2f;
                
                var sectorIndex = (int)(normalizedAngle / (Mathf.PI / 4f));
                sectorIndex = sectorIndex % 8; // Ensure in range 0-7
                
                // Set the primary sector
                state |= 1UL << (baseIndex + sectorIndex);
                
                // Check adjacent sectors based on finger radius
                // Each sector spans π/4 radians, check if finger overlaps neighbors
                var sectorAngle = Mathf.PI / 4f;
                var fingerAngleSpan = fingerRadius / radius; // Approximate angular span of finger
                
                if (fingerAngleSpan > sectorAngle * 0.3f)
                {
                    // Finger is large enough to potentially touch adjacent sectors
                    var leftIndex = (sectorIndex + 7) % 8;
                    var rightIndex = (sectorIndex + 1) % 8;
                    state |= 1UL << (baseIndex + leftIndex);
                    state |= 1UL << (baseIndex + rightIndex);
                }
                
                return state;
            }

            /// <summary>
            /// Update sensor states for this frame. Called from main thread OnPreUpdate.
            /// Synchronizes native callback thread data to main thread.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void OnPreUpdate()
            {
                // Check for screen size changes
                var currentWidth = Screen.width;
                var currentHeight = Screen.height;
                if (currentWidth != _screenWidth || currentHeight != _screenHeight)
                {
                    _screenWidth = currentWidth;
                    _screenHeight = currentHeight;
                    UpdateWorldTransform();
                }
                
                var isLocked = false;
                try
                {
                    _syncLock.Enter(ref isLocked);
                    
                    // Rebuild real-time states from active fingers FIRST
                    // This ensures we have current state before copying
                    Array.Clear(_sensorRealTimeStates, 0, 35);
                    foreach (var kvp in _fingerZoneStates)
                    {
                        var zoneState = kvp.Value;
                        for (var i = 0; i < 34; i++)
                        {
                            if ((zoneState & (1UL << i)) != 0)
                            {
                                _sensorRealTimeStates[i] = true;
                            }
                        }
                    }
                    
                    // Copy internal states to public states
                    Array.Copy(_isSensorHadOnInternal, _isSensorHadOn, 35);
                    Array.Copy(_isSensorHadOffInternal, _isSensorHadOff, 35);
                    Array.Copy(_sensorRealTimeStates, _sensorStates, 35);
                    Array.Copy(_sensorClickedCountInternal, _sensorClickedCount, 35);
                    Array.Copy(_extraButtonStatesInternal, _extraButtonStates, 12);
                    
                    // Clear internal accumulation arrays for next frame
                    Array.Clear(_isSensorHadOnInternal, 0, 35);
                    Array.Clear(_isSensorHadOffInternal, 0, 35);
                    Array.Clear(_sensorClickedCountInternal, 0, 35);
                    Array.Clear(_extraButtonStatesInternal, 0, 12);
                }
                finally
                {
                    if (isLocked)
                    {
                        _syncLock.Exit();
                    }
                }
            }
            
            /// <summary>
            /// Process touch events on the main thread for backward compatibility.
            /// This method now simply copies the pre-computed states.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ProcessTouchEvents(
                Camera mainCamera,
                Span<int> sensorClickedCount,
                Span<bool> sensorStates,
                Span<bool> extraButtonStates)
            {
                // Copy pre-computed sensor states
                for (var i = 0; i < 34; i++)
                {
                    sensorStates[i] = _sensorStates[i];
                    sensorClickedCount[i] = _sensorClickedCount[i];
                }
                
                // Copy extra button states
                for (var i = 0; i < extraButtonStates.Length && i < 12; i++)
                {
                    extraButtonStates[i] = _extraButtonStates[i];
                }
            }

            /// <summary>
            /// Get count of active touches
            /// </summary>
            public static int ActiveTouchCount => _fingerZoneStates.Count;

            /// <summary>
            /// Check if there are any active touches
            /// </summary>
            public static bool HasActiveTouches => !_fingerZoneStates.IsEmpty;
            
            #region Public State Access Methods (matching TouchPanel API)
            
            /// <summary>
            /// Determines whether the sensor at the given area was ever ON
            /// during the interval between the two most recent OnPreUpdate calls.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsHadOn(SensorArea area)
            {
                if (area < SensorArea.C)
                {
                    return _isSensorHadOn[(int)area];
                }
                else if (area == SensorArea.C)
                {
                    return _isSensorHadOn[16] || _isSensorHadOn[17];
                }
                else if (area <= SensorArea.E8)
                {
                    return _isSensorHadOn[(int)area + 1];
                }
                return false;
            }
            
            /// <summary>
            /// Determines whether the sensor at the given area is ON in this frame.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsOn(SensorArea area)
            {
                if (area < SensorArea.C)
                {
                    return _sensorStates[(int)area];
                }
                else if (area == SensorArea.C)
                {
                    return _sensorStates[16] || _sensorStates[17];
                }
                else if (area <= SensorArea.E8)
                {
                    return _sensorStates[(int)area + 1];
                }
                return false;
            }
            
            /// <summary>
            /// Determines whether the sensor at the given index was ever ON
            /// during the interval between the two most recent OnPreUpdate calls.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsHadOn(int index)
            {
                if (index < 0 || index > 33)
                    return false;
                return _isSensorHadOn[index];
            }
            
            /// <summary>
            /// Determines whether the sensor at the given index is ON in this frame.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsOn(int index)
            {
                if (index < 0 || index > 33)
                    return false;
                return _sensorStates[index];
            }
            
            #endregion
        }
    }
}
#endif

