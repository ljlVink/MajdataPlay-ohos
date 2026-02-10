#if UNITY_OPENHARMONY
using System;
using System.Runtime.InteropServices;
using AOT;
#nullable enable
namespace MajdataPlay.IO
{
    public static class OHInputInterop
    {
        private const string DllName = "ohinput";

        #region Enums
        public enum TouchAction
        {
            Cancel = 0,
            Down = 1,
            Move = 2,
            Up = 3
        }

        public enum InputResult
        {
            Success = 0,
            PermissionDenied = 201,
            ParameterError = 401,
            ServiceException = 3800001,
            RepeatInterceptor = 4200001
        }
        #endregion

        #region Callback Delegates
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Input_TouchEventCallback(IntPtr touchEvent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Input_MouseEventCallback(IntPtr mouseEvent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Input_AxisEventCallback(IntPtr axisEvent);
        #endregion

        #region Structures
        [StructLayout(LayoutKind.Sequential)]
        public struct Input_InterceptorEventCallback
        {
            public IntPtr mouseCallback;
            public IntPtr touchCallback;
            public IntPtr axisCallback;
        }
        #endregion

        #region Interceptor API
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_AddInputEventInterceptor(
            ref Input_InterceptorEventCallback callback,
            IntPtr option
        );

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_RemoveInputEventInterceptor();
        #endregion

        #region TouchEvent Data Extraction API
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_GetTouchEventAction(IntPtr touchEvent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_GetTouchEventFingerId(IntPtr touchEvent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_GetTouchEventDisplayX(IntPtr touchEvent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Input_GetTouchEventDisplayY(IntPtr touchEvent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long OH_Input_GetTouchEventActionTime(IntPtr touchEvent);
        #endregion
    }

    public struct OHTouchData
    {
        public int Action;
        public int FingerId;
        public int DisplayX;
        public int DisplayY;
        public long ActionTime;
        public OHInputInterop.TouchAction TouchAction => (OHInputInterop.TouchAction)Action;
        public float GetUnityY(int screenHeight) => screenHeight - DisplayY;
    }
}
#endif
