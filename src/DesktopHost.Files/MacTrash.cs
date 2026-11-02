using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopHost.Files;

/// <summary>
/// The macOS move to the person's Trash through <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>, called by way of
/// the Objective-C runtime (no native helper). The system records where the item came from, so Finder's «Put Back» works, and
/// the call needs no Automation permission, which an AppleEvent to Finder would ask the person for on first use.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacTrash
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    // Foundation's error domain for a file operation, and the two codes that mean «this volume cannot keep it» rather than «refused».
    private const long NsFeatureUnsupportedError = 3328;
    private const long NsFileWriteVolumeReadOnlyError = 642;

    /// <summary>Moves <paramref name="fullPath"/> to the Trash and returns where the system put it.</summary>
    public static string Move(string fullPath)
    {
        // Foundation is loaded by the process that runs a UI; a host without one has to load it before its classes exist.
        NativeLibrary.Load(Foundation);

        var pool = objc_autoreleasePoolPush();
        try
        {
            var nsString = objc_getClass("NSString");
            var nsUrl = objc_getClass("NSURL");
            var nsFileManager = objc_getClass("NSFileManager");

            var path = StringArg(nsString, fullPath);
            var url = SendPtr(nsUrl, Selector("fileURLWithPath:"), path);
            var manager = SendPtr(nsFileManager, Selector("defaultManager"));
            if (url == 0 || manager == 0)
                throw new TrashException(TrashException.Refused, "The system could not describe this item to the Trash.");

            nint resulting = 0;
            nint error = 0;
            var ok = SendTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, ref resulting, ref error);
            if (ok == 0)
                throw ToException(error);

            return resulting == 0 ? fullPath : StringOf(SendPtr(resulting, Selector("path"))) ?? fullPath;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static TrashException ToException(nint error)
    {
        if (error == 0)
            return new TrashException(TrashException.Refused, "The Trash did not take the item.");
        var code = SendLong(error, Selector("code"));
        var text = StringOf(SendPtr(error, Selector("localizedDescription"))) ?? "The Trash did not take the item.";
        var reason = code is NsFeatureUnsupportedError or NsFileWriteVolumeReadOnlyError
            ? TrashException.NotRecyclable
            : TrashException.Refused;
        return new TrashException(reason, text);
    }

    private static nint StringArg(nint nsString, string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return SendPtr(nsString, Selector("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? StringOf(nint nsString)
    {
        if (nsString == 0) return null;
        var utf8 = SendPtr(nsString, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static nint Selector(string name) => sel_registerName(name);

    // objc_msgSend is one function with a signature per call: each shape this file uses gets its own declaration.
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial long SendLong(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, ref nint resultingUrl, ref nint error);
}
