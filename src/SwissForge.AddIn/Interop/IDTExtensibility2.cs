using System;
using System.Runtime.InteropServices;

namespace SwissForge.AddIn.Interop
{
    /// <summary>How the host is loading the add-in.</summary>
    public enum ext_ConnectMode
    {
        ext_cm_AfterStartup = 0,
        ext_cm_Startup = 1,
        ext_cm_External = 2,
        ext_cm_CommandLine = 3,
        ext_cm_Solution = 4,
        ext_cm_UISetup = 5
    }

    /// <summary>Why the host is unloading the add-in.</summary>
    public enum ext_DisconnectMode
    {
        ext_dm_HostShutdown = 0,
        ext_dm_UserClosed = 1,
        ext_dm_UISetupComplete = 2,
        ext_dm_SolutionClosed = 3
    }

    /// <summary>
    /// The add-in contract ESPRIT loads, declared here rather than referenced.
    /// <para>
    /// The conventional route is a COM reference to Extensibility.dll (the Microsoft Add-In
    /// Designer type library). Declaring the interface directly with its published IID gives
    /// identical COM identity — the host binds by IID, not by which assembly declared it —
    /// while keeping this project free of any external reference. That matters because a
    /// missing or version-mismatched Extensibility.dll on one shop machine is a common and
    /// deeply unhelpful way for an add-in to fail to load.
    /// </para>
    /// <para>
    /// The method order below is the published vtable order and must not be changed.
    /// </para>
    /// </summary>
    [ComImport]
    [Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDTExtensibility2
    {
        void OnConnection(
            [MarshalAs(UnmanagedType.IDispatch)] object Application,
            ext_ConnectMode ConnectMode,
            [MarshalAs(UnmanagedType.IDispatch)] object AddInInst,
            [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        void OnDisconnection(
            ext_DisconnectMode RemoveMode,
            [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        void OnAddInsUpdate([MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        void OnStartupComplete([MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        void OnBeginShutdown([MarshalAs(UnmanagedType.SafeArray)] ref Array custom);
    }
}
