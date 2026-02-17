// File: ShareX.ScreenCaptureLib/Screenshot_HDR.cs
//
// HDR screenshot capture using DXGI Desktop Duplication.
// Uses [ComImport] interface declarations for DXGI interfaces (safe vtable
// dispatch via CLR) and raw vtable calls only for the few D3D11 methods
// needed (ID3D11Device has ~43 methods, making full [ComImport] brittle).
//
// Multi-monitor: DXGI Desktop Duplication is per-output. ShareX capture rects
// use virtual desktop coordinates that can span multiple monitors. This code
// enumerates all DXGI outputs, captures each monitor that intersects the
// requested rect, and composites the results onto a single bitmap.
//
// Capture sequence per output:
//   1. D3D11CreateDevice(null, HARDWARE)
//   2. QI to IDXGIDevice -> GetAdapter()
//   3. adapter.EnumOutputs(i) for each intersecting output
//   4. QI to IDXGIOutput5 -> DuplicateOutput1 with 3 format fallbacks
//   5. AcquireNextFrame (with retry loop for DWM warm-up) -> copy -> map -> convert

#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        public bool CaptureHDREnabled { get; set; } = false;

        // ====================================================================
        // DisplayConfig API for SDR White Level
        // ====================================================================

        #region DisplayConfig P/Invoke

        private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] modeInfoData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(int flags, out int numPaths, out int numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(int flags, ref int numPaths,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref int numModes,
            [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL info);

        #endregion

        // ====================================================================
        // COM Interface Declarations (proper [ComImport])
        // ====================================================================
        // The CLR uses these to build correct vtables automatically.
        // Method order must exactly match the IDL/header order.
        // We use [ComImport] for all DXGI interfaces (where correct vtable
        // dispatch is critical) and raw vtable calls for the few D3D11
        // methods we need (ID3D11Device has ~43 methods, making full
        // [ComImport] declaration impractical and error-prone).

        #region COM Interfaces

        // --- DXGI Interfaces ---

        [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIDevice
        {
            // IDXGIObject methods (4 methods: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIDevice methods
            int GetAdapter(out IDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            // IDXGIObject methods
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIAdapter methods
            int EnumOutputs(uint index, out IDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            // IDXGIObject (4 methods)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput methods (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput1
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4 methods)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
        }

        // IDXGIOutput5 inherits: IDXGIOutput4 -> IDXGIOutput3 -> IDXGIOutput2 -> IDXGIOutput1
        // IDXGIOutput2 adds: SupportsOverlays (1 method)
        // IDXGIOutput3 adds: CheckOverlaySupport (1 method)
        // IDXGIOutput4 adds: CheckOverlayColorSpaceSupport (1 method)
        // IDXGIOutput5 adds: DuplicateOutput1 (1 method)
        [ComImport, Guid("80A07424-AB52-42EB-833C-0C42FD282D98"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput5
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);

            // IDXGIOutput2 (1)
            [PreserveSig] int SupportsOverlays();

            // IDXGIOutput3 (1)
            int CheckOverlaySupport(uint format, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput4 (1)
            int CheckOverlayColorSpaceSupport(uint format, uint colorSpace, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput5 (1)
            int DuplicateOutput1([MarshalAs(UnmanagedType.IUnknown)] object device, uint flags,
                uint formatCount, [MarshalAs(UnmanagedType.LPArray)] int[] formats,
                out IDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutputDuplication
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutputDuplication methods
            void GetDesc(out DXGI_OUTDUPL_DESC desc);

            [PreserveSig]
            int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo,
                [MarshalAs(UnmanagedType.IUnknown)] out object resource);

            int GetFrameDirtyRects(uint dirtyRectsBufferSize, IntPtr dirtyRectsBuffer, out uint dirtyRectsBufferSizeRequired);
            int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr moveRectsBuffer, out uint moveRectsBufferSizeRequired);
            int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pointerShapeBuffer,
                out uint pointerShapeBufferSizeRequired, IntPtr pointerShapeInfo);
            int MapDesktopSurface(out DXGI_MAPPED_RECT mappedRect);
            int UnMapDesktopSurface();

            [PreserveSig]
            int ReleaseFrame();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_DESC
        {
            public DXGI_MODE_DESC ModeDesc;
            public uint Rotation;
            [MarshalAs(UnmanagedType.Bool)]
            public bool DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MODE_DESC
        {
            public uint Width, Height;
            public DXGI_RATIONAL RefreshRate;
            public uint Format;
            public uint ScanlineOrdering;
            public uint Scaling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ProtectedContentMaskedOut;
            public int PointerPositionX;
            public int PointerPositionY;
            [MarshalAs(UnmanagedType.Bool)]
            public bool PointerPositionVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MAPPED_RECT
        {
            public int Pitch;
            public IntPtr pBits;
        }

        #endregion

        // ====================================================================
        // D3D11 Types and Imports (raw vtable for the few calls we need)
        // ====================================================================

        #region D3D11

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            int[] pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_SDK_VERSION = 7;

        private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
        private const int DXGI_FORMAT_R10G10B10A2_UNORM = 24;
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const int D3D11_MAP_READ = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        // ID3D11Texture2D::GetDesc -- vtable slot 10
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_GetTexDesc(IntPtr self, out D3D11_TEXTURE2D_DESC desc);

        // ID3D11Device::CreateTexture2D -- vtable slot 5
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateTex2D(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);

        // ID3D11DeviceContext::Map -- vtable slot 14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);

        // ID3D11DeviceContext::Unmap -- vtable slot 15
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_Unmap(IntPtr self, IntPtr res, uint sub);

        // ID3D11DeviceContext::CopyResource -- vtable slot 47
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_CopyResource(IntPtr self, IntPtr dst, IntPtr src);

        private static IntPtr VT(IntPtr obj, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

        #endregion

        // ====================================================================
        // Main HDR Capture (multi-monitor aware)
        // ====================================================================

        private static float GetSdrWhiteNits()
        {
            try
            {
                int r = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out int pc, out int mc);
                if (r != 0) return 80f;
                var paths = new DISPLAYCONFIG_PATH_INFO[pc];
                var modes = new DISPLAYCONFIG_MODE_INFO[mc];
                r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (r != 0) return 80f;

                for (int i = 0; i < pc; i++)
                {
                    var info = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                    info.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                    info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>();
                    info.header.adapterId = paths[i].targetInfo.adapterId;
                    info.header.id = paths[i].targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref info) == 0 && info.SDRWhiteLevel > 0)
                    {
                        float nits = (info.SDRWhiteLevel / 1000f) * 80f;
                        DebugHelper.WriteLine($"HDR: SDR white level = {nits} nits (raw {info.SDRWhiteLevel})");
                        return nits;
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: SDR white level query failed.");
            }
            return 80f;
        }

        /// <summary>
        /// Captures the specified rectangle in virtual desktop coordinates using
        /// DXGI Desktop Duplication. Handles multi-monitor setups by enumerating
        /// all outputs, capturing each intersecting monitor, and compositing the
        /// results onto a single bitmap.
        /// </summary>
        public Bitmap CaptureRectangleHDR(Rectangle rect)
        {
            IntPtr devicePtr = IntPtr.Zero;
            IntPtr contextPtr = IntPtr.Zero;

            try
            {
                float sdrWhiteNits = GetSdrWhiteNits();
                float normScale = 80f / sdrWhiteNits;
                DebugHelper.WriteLine($"HDR: normScale={normScale} (sdrWhite={sdrWhiteNits})");

                // Step 1: Create D3D11 device
                int[] levels = { 0xb100, 0xb000 };
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    levels, (uint)levels.Length, D3D11_SDK_VERSION,
                    out devicePtr, out _, out contextPtr);
                if (hr != 0)
                {
                    DebugHelper.WriteLine($"HDR: D3D11CreateDevice failed 0x{hr:X8}");
                    return null;
                }

                // Step 2: Get DXGI adapter
                var dxgiDevice = (IDXGIDevice)Marshal.GetObjectForIUnknown(devicePtr);
                hr = dxgiDevice.GetAdapter(out IDXGIAdapter adapter);
                if (hr != 0)
                {
                    DebugHelper.WriteLine($"HDR: GetAdapter failed 0x{hr:X8}");
                    return null;
                }

                // Create the final composite bitmap matching the requested rect size
                Bitmap result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                bool anyOutputCaptured = false;

                // Step 3: Enumerate all outputs, capture each that intersects our rect.
                // Note: EnumOutputs is declared via [ComImport] without [PreserveSig],
                // so the CLR throws COMException for non-zero HRESULTs instead of
                // returning them. DXGI_ERROR_NOT_FOUND is the normal end-of-list signal.
                for (uint outputIdx = 0; ; outputIdx++)
                {
                    IDXGIOutput output;
                    try
                    {
                        hr = adapter.EnumOutputs(outputIdx, out output);
                    }
                    catch (COMException ex) when (ex.HResult == DXGI_ERROR_NOT_FOUND)
                    {
                        break;
                    }
                    if (hr == DXGI_ERROR_NOT_FOUND || output == null)
                    {
                        break;
                    }
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR: EnumOutputs({outputIdx}) failed 0x{hr:X8}");
                        break;
                    }

                    try
                    {
                        output.GetDesc(out DXGI_OUTPUT_DESC outputDesc);
                        Rectangle monitorRect = new Rectangle(
                            outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Top,
                            outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top);

                        // Check if this monitor intersects the capture rect
                        Rectangle intersection = Rectangle.Intersect(rect, monitorRect);
                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) does not intersect capture rect, skipping.");
                            continue;
                        }

                        DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) {monitorRect}, intersection={intersection}");

                        // Probe the output's format to decide the capture path.
                        // SDR monitors (B8G8R8A8) get a fast GDI BitBlt instead of
                        // the full DXGI duplication pipeline with warm-up delays.
                        int probeFormat = ProbeOutputFormat(devicePtr, output);
                        bool ok;

                        if (probeFormat == DXGI_FORMAT_B8G8R8A8_UNORM)
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} is SDR (format={probeFormat}), using GDI fast path.");
                            ok = CaptureOutputGDI(intersection, rect, result);
                        }
                        else
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} is HDR (format={probeFormat}), using DXGI pipeline.");
                            ok = CaptureOutputHDR(devicePtr, contextPtr, output,
                                monitorRect, rect, intersection, result, normScale);
                        }

                        if (ok)
                        {
                            anyOutputCaptured = true;
                        }
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, $"HDR: Output {outputIdx} capture failed, continuing.");
                    }
                }

                if (!anyOutputCaptured)
                {
                    DebugHelper.WriteLine("HDR: No outputs captured successfully.");
                    result.Dispose();
                    return null;
                }

                DebugHelper.WriteLine($"HDR: Composite output {result.Width}x{result.Height}");
                return result;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR capture failed.");
                return null;
            }
            finally
            {
                if (contextPtr != IntPtr.Zero) Marshal.Release(contextPtr);
                if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);
            }
        }

        /// <summary>
        /// Probes a DXGI output to determine its pixel format via DuplicateOutput1.
        /// Returns the negotiated DXGI format, or DXGI_FORMAT_B8G8R8A8_UNORM as fallback.
        /// The duplication handle is released immediately since we only need the format.
        /// </summary>
        private static int ProbeOutputFormat(IntPtr devicePtr, IDXGIOutput output)
        {
            try
            {
                var output5 = (IDXGIOutput5)output;
                int[] formats = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
                int hr = output5.DuplicateOutput1(Marshal.GetObjectForIUnknown(devicePtr), 0,
                    (uint)formats.Length, formats, out IDXGIOutputDuplication probeDupl);
                if (hr == 0 && probeDupl != null)
                {
                    probeDupl.GetDesc(out DXGI_OUTDUPL_DESC dd);
                    Marshal.ReleaseComObject(probeDupl);
                    return (int)dd.ModeDesc.Format;
                }
            }
            catch (InvalidCastException)
            {
                // IDXGIOutput5 not available
            }
            catch (Exception)
            {
                // Probe failed
            }
            return DXGI_FORMAT_B8G8R8A8_UNORM;
        }

        /// <summary>
        /// Captures an SDR output region using GDI BitBlt and draws it into the
        /// composite bitmap. Much faster than the full DXGI duplication pipeline
        /// for monitors that don't need HDR conversion.
        /// </summary>
        private bool CaptureOutputGDI(Rectangle intersection, Rectangle captureRect, Bitmap composite)
        {
            int dstX = intersection.X - captureRect.X;
            int dstY = intersection.Y - captureRect.Y;

            try
            {
                // Use GDI BitBlt to capture the SDR region. CaptureRectangleNative
                // uses virtual desktop coordinates, which is exactly what we have.
                using (Bitmap sdrCapture = CaptureRectangleNative(intersection, false))
                {
                    if (sdrCapture == null) return false;

                    using (Graphics g = Graphics.FromImage(composite))
                    {
                        g.DrawImageUnscaled(sdrCapture, dstX, dstY);
                    }
                }

                DebugHelper.WriteLine($"HDR: SDR output captured via GDI ({intersection.Width}x{intersection.Height})");
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: GDI fallback for SDR output failed.");
                return false;
            }
        }

        /// <summary>
        /// Captures a single HDR DXGI output and blits the intersecting region into
        /// the composite bitmap at the correct offset. Only used for outputs that
        /// negotiate an HDR format (RGBA16F or R10G10B10A2).
        /// </summary>
        private bool CaptureOutputHDR(IntPtr devicePtr, IntPtr contextPtr,
            IDXGIOutput output, Rectangle monitorRect, Rectangle captureRect,
            Rectangle intersection, Bitmap composite, float normScale)
        {
            IDXGIOutputDuplication duplication = null;
            IntPtr stagingPtr = IntPtr.Zero;

            try
            {
                // Duplicate the output
                bool gotDuplication = false;
                int texFormat = DXGI_FORMAT_B8G8R8A8_UNORM;

                try
                {
                    var output5 = (IDXGIOutput5)output;
                    int[] formats = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
                    int hr = output5.DuplicateOutput1(Marshal.GetObjectForIUnknown(devicePtr), 0,
                        (uint)formats.Length, formats, out duplication);
                    if (hr == 0)
                    {
                        gotDuplication = true;
                        duplication.GetDesc(out DXGI_OUTDUPL_DESC dd);
                        texFormat = (int)dd.ModeDesc.Format;
                        DebugHelper.WriteLine($"HDR: DuplicateOutput1 OK, format={texFormat}");
                    }
                    else
                    {
                        DebugHelper.WriteLine($"HDR: DuplicateOutput1 failed 0x{hr:X8}, trying legacy.");
                    }
                }
                catch (InvalidCastException)
                {
                    DebugHelper.WriteLine("HDR: IDXGIOutput5 not supported, trying legacy.");
                }

                if (!gotDuplication)
                {
                    var output1 = (IDXGIOutput1)output;
                    int hr = output1.DuplicateOutput(Marshal.GetObjectForIUnknown(devicePtr), out duplication);
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR: DuplicateOutput failed 0x{hr:X8}");
                        return false;
                    }
                    duplication.GetDesc(out DXGI_OUTDUPL_DESC dd);
                    texFormat = (int)dd.ModeDesc.Format;
                    DebugHelper.WriteLine($"HDR: DuplicateOutput (legacy) OK, format={texFormat}");
                }

                // Warm-up and frame acquisition with retry loop.
                // DWM often returns a black/empty frame immediately after duplication
                // init. We retry up to 30 times, discarding frames where
                // LastPresentTime == 0 (no new pixel data from the compositor).
                Thread.Sleep(50);

                object resourceObj = null;

                for (int attempt = 0; attempt < 30; attempt++)
                {
                    int acquireHr = duplication.AcquireNextFrame(200, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out resourceObj);

                    if (acquireHr == 0)
                    {
                        if (frameInfo.LastPresentTime == 0 && attempt < 5)
                        {
                            if (resourceObj != null)
                            {
                                Marshal.ReleaseComObject(resourceObj);
                                resourceObj = null;
                            }
                            duplication.ReleaseFrame();
                            Thread.Sleep(50);
                            continue;
                        }
                        break;
                    }

                    if (acquireHr == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        continue;
                    }

                    Thread.Sleep(50);
                }

                if (resourceObj == null)
                {
                    DebugHelper.WriteLine("HDR: Timed out acquiring valid frame.");
                    return false;
                }

                IntPtr resourcePtr = Marshal.GetIUnknownForObject(resourceObj);
                try
                {
                    Guid texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    Marshal.QueryInterface(resourcePtr, in texGuid, out IntPtr texPtr);

                    try
                    {
                        var getDesc = Marshal.GetDelegateForFunctionPointer<Del_GetTexDesc>(VT(texPtr, 10));
                        getDesc(texPtr, out D3D11_TEXTURE2D_DESC td);
                        int texW = (int)td.Width, texH = (int)td.Height;
                        DebugHelper.WriteLine($"HDR: Texture {texW}x{texH} format={td.Format}");

                        // Create staging texture
                        td.Usage = D3D11_USAGE_STAGING;
                        td.BindFlags = 0;
                        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                        td.MiscFlags = 0;
                        var createTex = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5));
                        int hr2 = createTex(devicePtr, ref td, IntPtr.Zero, out stagingPtr);
                        if (hr2 != 0)
                        {
                            DebugHelper.WriteLine($"HDR: CreateTexture2D staging failed 0x{hr2:X8}");
                            return false;
                        }

                        // Copy and map
                        var copy = Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47));
                        copy(contextPtr, stagingPtr, texPtr);

                        var map = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14));
                        hr2 = map(contextPtr, stagingPtr, 0, D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                        if (hr2 != 0)
                        {
                            DebugHelper.WriteLine($"HDR: Map failed 0x{hr2:X8}");
                            return false;
                        }

                        try
                        {
                            // The intersection is in virtual desktop coordinates.
                            // Source offset within the monitor texture: subtract
                            // the monitor's top-left since the texture starts at (0,0).
                            int srcX = intersection.X - monitorRect.X;
                            int srcY = intersection.Y - monitorRect.Y;
                            int copyW = Math.Min(intersection.Width, texW - srcX);
                            int copyH = Math.Min(intersection.Height, texH - srcY);

                            // Destination offset within the composite bitmap:
                            // subtract the overall capture rect's top-left.
                            int dstX = intersection.X - captureRect.X;
                            int dstY = intersection.Y - captureRect.Y;

                            if (copyW > 0 && copyH > 0)
                            {
                                BlitHDRToComposite(mapped.pData, (int)mapped.RowPitch, texW, texH,
                                    td.Format, srcX, srcY, copyW, copyH,
                                    composite, dstX, dstY, normScale);
                            }

                            return true;
                        }
                        finally
                        {
                            var unmap = Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15));
                            unmap(contextPtr, stagingPtr, 0);
                        }
                    }
                    finally
                    {
                        Marshal.Release(texPtr);
                    }
                }
                finally
                {
                    Marshal.Release(resourcePtr);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: Output capture failed.");
                return false;
            }
            finally
            {
                if (duplication != null)
                {
                    try { duplication.ReleaseFrame(); } catch { }
                    Marshal.ReleaseComObject(duplication);
                }
                if (stagingPtr != IntPtr.Zero) Marshal.Release(stagingPtr);
            }
        }

        // ====================================================================
        // Pixel Conversion
        // ====================================================================

        /// <summary>
        /// Converts and blits a region from the mapped HDR texture directly into
        /// the composite bitmap at the specified destination offset.
        /// </summary>
        private static void BlitHDRToComposite(IntPtr data, int rowPitch, int texW, int texH,
            int fmt, int srcX, int srcY, int copyW, int copyH,
            Bitmap composite, int dstX, int dstY, float normScale)
        {
            // Clamp to valid ranges
            if (srcX < 0) { dstX -= srcX; copyW += srcX; srcX = 0; }
            if (srcY < 0) { dstY -= srcY; copyH += srcY; srcY = 0; }
            copyW = Math.Min(copyW, texW - srcX);
            copyH = Math.Min(copyH, texH - srcY);
            copyW = Math.Min(copyW, composite.Width - dstX);
            copyH = Math.Min(copyH, composite.Height - dstY);
            if (copyW <= 0 || copyH <= 0) return;

            var bd = composite.LockBits(new Rectangle(dstX, dstY, copyW, copyH),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    for (int y = 0; y < copyH; y++)
                    {
                        byte* dst = (byte*)bd.Scan0 + y * bd.Stride;

                        if (fmt == DXGI_FORMAT_R16G16B16A16_FLOAT)
                        {
                            // scRGB (linear, scene-referred). Normalize by SDR white level,
                            // tonemap highlights, convert to sRGB.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 8;
                            for (int x = 0; x < copyW; x++)
                            {
                                ushort* p = (ushort*)(src + x * 8);
                                float r = HalfToFloat(p[0]);
                                float g = HalfToFloat(p[1]);
                                float b = HalfToFloat(p[2]);
                                float a = HalfToFloat(p[3]);

                                r = Math.Max(r * normScale, 0f);
                                g = Math.Max(g * normScale, 0f);
                                b = Math.Max(b * normScale, 0f);

                                TonemapBT2390(ref r, ref g, ref b);

                                r = LinearToSRGB(r);
                                g = LinearToSRGB(g);
                                b = LinearToSRGB(b);

                                dst[x * 4 + 0] = FloatToByte(b);
                                dst[x * 4 + 1] = FloatToByte(g);
                                dst[x * 4 + 2] = FloatToByte(r);
                                dst[x * 4 + 3] = FloatToByte(Math.Clamp(a, 0f, 1f));
                            }
                        }
                        else if (fmt == DXGI_FORMAT_R10G10B10A2_UNORM)
                        {
                            // HDR10: PQ (ST.2084) encoded, BT.2020 gamut.
                            // Decode PQ to linear nits, convert BT.2020 -> BT.709,
                            // normalize to [0,1] by dividing by 80 nits, tonemap, sRGB encode.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 4;
                            for (int x = 0; x < copyW; x++)
                            {
                                uint pixel = *(uint*)(src + x * 4);
                                float rN = PQ_EOTF((pixel & 0x3FFu) / 1023f);
                                float gN = PQ_EOTF(((pixel >> 10) & 0x3FFu) / 1023f);
                                float bN = PQ_EOTF(((pixel >> 20) & 0x3FFu) / 1023f);

                                // BT.2020 to BT.709 color matrix, result in nits
                                float r = (1.6605f * rN - 0.5877f * gN - 0.0728f * bN) / 80f;
                                float g = (-0.1246f * rN + 1.1330f * gN - 0.0084f * bN) / 80f;
                                float b = (-0.0182f * rN - 0.1006f * gN + 1.1187f * bN) / 80f;

                                r = Math.Max(r, 0f);
                                g = Math.Max(g, 0f);
                                b = Math.Max(b, 0f);

                                TonemapBT2390(ref r, ref g, ref b);

                                r = LinearToSRGB(r);
                                g = LinearToSRGB(g);
                                b = LinearToSRGB(b);

                                dst[x * 4 + 0] = FloatToByte(b);
                                dst[x * 4 + 1] = FloatToByte(g);
                                dst[x * 4 + 2] = FloatToByte(r);
                                dst[x * 4 + 3] = 255;
                            }
                        }
                        else
                        {
                            // SDR (B8G8R8A8_UNORM). Already in the right layout for
                            // Format32bppArgb, just copy directly.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 4;
                            Buffer.MemoryCopy(src, dst, bd.Stride, copyW * 4);
                        }
                    }
                }
            }
            finally
            {
                composite.UnlockBits(bd);
            }
        }

        // ====================================================================
        // Color Math
        // ====================================================================

        /// <summary>
        /// BT.2390 style luminance-based tonemap. SDR content (luminance &lt;= 1.0)
        /// passes through untouched. Only highlights above 1.0 are compressed,
        /// capped at 1.5 to avoid hard clipping artifacts.
        /// </summary>
        private static void TonemapBT2390(ref float r, ref float g, ref float b)
        {
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            if (lum > 1.0f)
            {
                float excess = lum - 1.0f;
                float compressed = 1.0f + excess / (1.0f + excess);
                if (compressed > 1.5f) compressed = 1.5f;

                float scale = compressed / lum;
                r *= scale;
                g *= scale;
                b *= scale;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        /// <summary>
        /// ST.2084 (PQ) Electro-Optical Transfer Function.
        /// Converts PQ encoded value [0,1] to absolute luminance in nits [0,10000].
        /// </summary>
        private static float PQ_EOTF(float N)
        {
            const float m1 = 0.1593017578125f;
            const float m2 = 78.84375f;
            const float c1 = 0.8359375f;
            const float c2 = 18.8515625f;
            const float c3 = 18.6875f;

            float Np = MathF.Pow(Math.Max(N, 0f), 1f / m2);
            float num = Math.Max(Np - c1, 0f);
            float den = c2 - c3 * Np;
            if (den <= 0f) return 0f;
            return MathF.Pow(num / den, 1f / m1) * 10000f;
        }

        private static float LinearToSRGB(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x <= 0.0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
        }

        private static unsafe float HalfToFloat(ushort h)
        {
            uint sign = ((uint)h & 0x8000u) << 16;
            uint exp = ((uint)h >> 10) & 0x1F;
            uint man = (uint)h & 0x3FF;
            uint result;

            if (exp == 0)
            {
                if (man == 0) { result = sign; return *(float*)&result; }
                while ((man & 0x400) == 0) { man <<= 1; exp--; }
                exp++; man &= ~0x400u; exp += 127 - 15;
                result = sign | (exp << 23) | (man << 13);
            }
            else if (exp == 31)
            {
                result = sign | 0x7F800000u | (man << 13);
            }
            else
            {
                exp += 127 - 15;
                result = sign | (exp << 23) | (man << 13);
            }

            return *(float*)&result;
        }

        private static byte FloatToByte(float v)
        {
            return (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
        }
    }
}