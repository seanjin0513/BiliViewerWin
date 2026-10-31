// SystemVolume.cs —— 系统主音量（Windows Core Audio API，零第三方依赖）
//
// 设计要点：
//  * 仅用 BCL + COM interop + P/Invoke(ole32.dll)，主项目 glob 编译即可，不引 NuGet。
//  * 每次公开调用都重新创建并释放 COM 对象（简单可靠，代价可忽略）。
//  * 所有失败路径静默返回 false / -1，绝不把异常抛给调用方。
//  * Changed 由 500ms 轮询 Timer 触发（不实现 IAudioEndpointVolumeCallback，
//    省掉 COM 回调的租约/线程问题）。订阅者是在 Timer（线程池）线程收到回调，
//    如果要在 UI 上刷新 HUD，请自行 Dispatcher.Invoke 切回 UI 线程。
//  * 本类自己调用 TrySet/Adjust 造成的音量变化不会触发 Changed（避免 HUD 重复刷新）；
//    外部程序或系统热键改音量、切换默认设备时才会触发。

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BiliViewer
{
    internal static class SystemVolume
    {
        // ───────────────────────── 公开接口 ─────────────────────────

        /// <summary>音量变化回调，参数为 0.0~1.0 的新音量。回调线程 = Timer 线程。</summary>
        public static event Action<double> Changed;

        /// <summary>当前系统主音量 0.0~1.0。</summary>
        public static bool TryGet(out double volume)
        {
            volume = 0.0;
            EnsureWatcher();
            int owned = EnterCom();
            try
            {
                using (Endpoint ep = Endpoint.Open())
                {
                    if (ep == null) return false;
                    float f;
                    if (ep.Volume.GetMasterVolumeLevelScalar(out f) != 0) return false;
                    volume = Clamp01(f);
                    return true;
                }
            }
            catch { return false; }
            finally { LeaveCom(owned); }
        }

        /// <summary>设置系统主音量（内部 clamp 到 0~1），成功返回 true。</summary>
        public static bool TrySet(double volume)
        {
            if (double.IsNaN(volume)) return false;
            EnsureWatcher();
            double target = Clamp01(volume);
            int owned = EnterCom();
            try
            {
                using (Endpoint ep = Endpoint.Open())
                {
                    if (ep == null) return false;
                    if (ep.Volume.SetMasterVolumeLevelScalar((float)target, IntPtr.Zero) != 0) return false;
                    // 回读实际值当基线（某些设备会量化到音量步进），自己改的不再触发 Changed
                    float f;
                    MarkBaseline(ep.Volume.GetMasterVolumeLevelScalar(out f) == 0 ? (double)f : target);
                    return true;
                }
            }
            catch { return false; }
            finally { LeaveCom(owned); }
        }

        /// <summary>相对调整（delta 可为负），返回调整后的音量 0.0~1.0；失败返回 -1。</summary>
        public static double Adjust(double delta)
        {
            try
            {
                if (double.IsNaN(delta) || double.IsInfinity(delta)) return -1.0;
                double cur;
                if (!TryGet(out cur)) return -1.0;
                double target = Clamp01(cur + delta);
                if (!TrySet(target)) return -1.0;
                // 回读为准：设备可能把 0.44 量化成别的步进值
                double after;
                if (TryGet(out after)) return after;
                return target;
            }
            catch { return -1.0; }
        }

        // ───────────────────────── 轮询 / 事件 ─────────────────────────

        private const int PollIntervalMs = 500;   // 轮询周期
        private const double ChangeEpsilon = 0.0005; // 小于半个音量步进不算变化
        private static readonly object s_gate = new object();
        private static Timer s_pollTimer;         // 静态字段持有即防止 Timer 被 GC
        private static double s_last = -1.0;      // 上次音量，<0 表示还没建立基线

        private static void EnsureWatcher()
        {
            if (s_pollTimer != null) return;
            lock (s_gate)
            {
                if (s_pollTimer != null) return;
                try
                {
                    // 不用 Timer 的初始延迟做「首测」，Poll 内部首轮只建基线
                    s_pollTimer = new Timer(Poll, null, PollIntervalMs, PollIntervalMs);
                }
                catch { }
            }
        }

        private static void Poll(object state)
        {
            int owned = EnterCom();
            try
            {
                double v;
                if (!TryGet(out v)) return; // TryGet 内层 EnterCom 得到 S_FALSE，不会重复 CoUninitialize
                double prev;
                lock (s_gate)
                {
                    prev = s_last;
                    s_last = v;
                }
                if (prev < 0.0) return;              // 首次只建立基线，不触发
                if (Math.Abs(v - prev) < ChangeEpsilon) return;
                Raise(v);
            }
            catch { }
            finally { LeaveCom(owned); }
        }

        private static void MarkBaseline(double v)
        {
            lock (s_gate) { s_last = v; }
        }

        private static void Raise(double v)
        {
            Action<double> handler = Changed;
            if (handler == null) return;
            // 逐个调用，单个订阅者抛异常不影响其它订阅者，也不影响轮询线程
            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<double>)list[i])(v); } catch { }
            }
        }

        // ───────────────────────── COM 初始化 ─────────────────────────

        private const int S_OK = 0;
        private const uint COINIT_MULTITHREADED = 0x0;
        private const uint CLSCTX_ALL = 0x17; // INPROC_SERVER|INPROC_HANDLER|LOCAL_SERVER|REMOTE_SERVER = 23

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        /// <summary>
        /// 确保当前线程有 COM 套间。返回 1 表示「本次调用初始化成功、退出时必须 CoUninitialize」。
        /// 返回 0 表示线程已被本进程/CLR 初始化过（S_FALSE=1 或 RPC_E_CHANGED_MODE=0x80010106）：
        /// 这种情况绝不能 CoUninitialize，否则会把别人的引用计数减掉。
        /// </summary>
        private static int EnterCom()
        {
            try
            {
                if (CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED) == S_OK) return 1;
            }
            catch { }
            return 0;
        }

        private static void LeaveCom(int owned)
        {
            if (owned != 1) return;
            try { CoUninitialize(); } catch { }
        }

        private static double Clamp01(double v)
        {
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }

        // ───────────────────────── 端点封装（每次调用新建 + 释放） ─────────────────────────

        private sealed class Endpoint : IDisposable
        {
            private IMMDeviceEnumerator _enumerator;
            private IMMDevice _device;
            private IAudioEndpointVolume _volume;

            public IAudioEndpointVolume Volume { get { return _volume; } }

            /// <summary>打开默认「多媒体」播放端点。任一步失败返回 null（已自行清理）。</summary>
            public static Endpoint Open()
            {
                Endpoint ep = new Endpoint();
                try
                {
                    Guid clsid = CLSID_MMDeviceEnumerator;
                    Guid iidEnum = IID_IMMDeviceEnumerator;
                    object obj;
                    if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iidEnum, out obj) != 0 || obj == null)
                    {
                        ep.Dispose();
                        return null;
                    }
                    ep._enumerator = (IMMDeviceEnumerator)obj;

                    if (ep._enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out ep._device) != 0
                        || ep._device == null)
                    {
                        ep.Dispose();
                        return null;
                    }

                    Guid iidVol = IID_IAudioEndpointVolume;
                    object vol;
                    if (ep._device.Activate(ref iidVol, CLSCTX_ALL, IntPtr.Zero, out vol) != 0 || vol == null)
                    {
                        ep.Dispose();
                        return null;
                    }
                    ep._volume = (IAudioEndpointVolume)vol;
                    return ep;
                }
                catch
                {
                    ep.Dispose();
                    return null;
                }
            }

            public void Dispose()
            {
                Release(_volume); _volume = null;
                Release(_device); _device = null;
                Release(_enumerator); _enumerator = null;
            }

            private static void Release(object o)
            {
                if (o == null) return;
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

        // ───────────────────────── COM 常量 ─────────────────────────

        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IMMDevice = new Guid("D666063F-1587-4E43-81F1-B948E807363F");
        private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        // ───────────────────────── COM 接口声明 ─────────────────────────
        // 关键：vtable 顺序 = idl 声明顺序，且**必须把接口的所有方法按顺序全部声明**，
        //      少声明或调换顺序都会让槽位错位，调用到别的函数地址（读到的音量是垃圾 / 直接崩）。
        //      下面注释里的「槽 N」是 0 基 vtable 下标，QI=0、AddRef=1、Release=2。

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            // 官方 idl(mmcdeviceapi.h) 顺序共 5 个方法：
            // 1 EnumAudioEndpoints                       -> 槽 3
            // 2 GetDefaultAudioEndpoint                  -> 槽 4
            // 3 GetDevice                                -> 槽 5
            // 4 RegisterEndpointNotificationCallback     -> 槽 6
            // 5 UnregisterEndpointNotificationCallback   -> 槽 7
            // 注意：RegisterControlChangeNotify 不在这里，它是 IAudioEndpointVolume 的第 1 个方法。
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            // 1 Activate            -> 槽 3（pActivationParams 是 PROPVARIANT*，这里传 null）
            // 2 OpenPropertyStore   -> 槽 4
            // 3 GetId               -> 槽 5
            // 4 GetState            -> 槽 6
            [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
                                       [MarshalAs(UnmanagedType.Interface)] out object ppInterface);
            [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig] int GetState(out uint pdwState);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            // 官方 idl(endpointvolume.h) 顺序，共 18 个方法，一个都不能少：
            //  1 RegisterControlChangeNotify        -> 槽 3
            //  2 UnregisterControlChangeNotify      -> 槽 4
            //  3 GetChannelCount                    -> 槽 5
            //  4 SetMasterVolumeLevel               -> 槽 6
            //  5 SetMasterVolumeLevelScalar         -> 槽 7   << 写音量（在 Get 之前！）
            //  6 GetMasterVolumeLevel               -> 槽 8
            //  7 GetMasterVolumeLevelScalar         -> 槽 9   << 读音量
            //  8 SetChannelVolumeLevel              -> 槽 10
            //  9 SetChannelVolumeLevelScalar        -> 槽 11
            // 10 GetChannelVolumeLevel              -> 槽 12
            // 11 GetChannelVolumeLevelScalar        -> 槽 13
            // 12 SetMute                            -> 槽 14
            // 13 GetMute                            -> 槽 15
            // 14 GetVolumeStepInfo                  -> 槽 16
            // 15 VolumeStepUp                       -> 槽 17
            // 16 VolumeStepDown                     -> 槽 18
            // 17 QueryHardwareSupport               -> 槽 19
            // 18 GetVolumeRange                     -> 槽 20
            [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, IntPtr pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, IntPtr pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute(int bMute, IntPtr pguidEventContext);
            [PreserveSig] int GetMute(out int pbMute);
            [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            [PreserveSig] int VolumeStepUp(IntPtr pguidEventContext);
            [PreserveSig] int VolumeStepDown(IntPtr pguidEventContext);
            [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }
    }
}
