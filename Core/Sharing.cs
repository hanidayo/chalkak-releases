using System;
using System.Windows;
using System.Windows.Interop;
#if CHALKAK_SHARE
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;
#endif

namespace Chalkak
{
#if CHALKAK_SHARE
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
#endif

    internal static class Sharing
    {
        public static bool Supported =>
#if CHALKAK_SHARE
            true;
#else
            false;
#endif

        /// <summary>Windows 공유 창을 연다. 사용자가 공유 대상을 고르기 전에는 아무것도 전송하지 않는다. 열지 못하면 false.</summary>
        public static bool ShareFile(Window owner, string path, string title)
        {
#if CHALKAK_SHARE
            try
            {
                IntPtr hwnd = new WindowInteropHelper(owner).Handle;
                var interop = DataTransferManager.As<IDataTransferManagerInterop>();
                Guid iid = new Guid(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);
                IntPtr ptr = interop.GetForWindow(hwnd, ref iid);
                var dtm = MarshalInterface<DataTransferManager>.FromAbi(ptr);

                TypedEventHandler<DataTransferManager, DataRequestedEventArgs> handler = null;
                handler = async (sender, args) =>
                {
                    var deferral = args.Request.GetDeferral();
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        args.Request.Data.Properties.Title = title;
                        args.Request.Data.SetStorageItems(new IStorageItem[] { file });
                        args.Request.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    }
                    catch { }
                    finally { deferral.Complete(); sender.DataRequested -= handler; }
                };
                dtm.DataRequested += handler;
                interop.ShowShareUIForWindow(hwnd);
                return true;
            }
            catch { return false; }
#else
            return false;
#endif
        }
    }
}
