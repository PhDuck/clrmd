﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime.Builders;
using Microsoft.Diagnostics.Runtime.Utilities;

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// Represents information about a single CLR in a process.
    /// </summary>
    public sealed class ClrInfo
    {
        private const string c_desktopModuleName = "clr.dll";
        private const string c_coreModuleName = "coreclr.dll";
        private const string c_linuxCoreModuleName = "libcoreclr.so";
        private const string c_macOSCoreModuleName = "libcoreclr.dylib";

        private const string c_desktopDacFileNameBase = "mscordacwks";
        private const string c_coreDacFileNameBase = "mscordaccore";
        private const string c_desktopDacFileName = c_desktopDacFileNameBase + ".dll";
        private const string c_coreDacFileName = c_coreDacFileNameBase + ".dll";
        private const string c_linuxCoreDacFileName = "libmscordaccore.so";
        private const string c_macOSCoreDacFileName = "libmscordaccore.dylib";

        private const string c_windowsDbiFileName = "mscordbi.dll";
        private const string c_linuxCoreDbiFileName = "libmscordbi.so";
        private const string c_macOSCoreDbiFileName = "libmscordbi.dylib";


        internal ClrInfo(DataTarget dt, ClrFlavor flavor, ModuleInfo module, ulong runtimeInfo)
        {
            DataTarget = dt ?? throw new ArgumentNullException(nameof(dt));
            Flavor = flavor;
            ModuleInfo = module ?? throw new ArgumentNullException(nameof(module));
            IsSingleFile = runtimeInfo != 0;

            List<DebugLibraryInfo> artifacts = new List<DebugLibraryInfo>(8);

            OSPlatform currentPlatform = GetCurrentPlatform();
            OSPlatform targetPlatform = dt.DataReader.TargetPlatform;
            Architecture currentArch = RuntimeInformation.ProcessArchitecture;
            Architecture targetArch = dt.DataReader.Architecture;

            string? dacCurrentPlatform = GetDacFileName(flavor, currentPlatform);
            string? dacTargetPlatform = GetDacFileName(flavor, targetPlatform);
            string? dbiTargetPlatform = GetDbiFileName(flavor, targetPlatform);
            if (IsSingleFile)
            {
                ClrRuntimeInfo info = DataTarget.DataReader.Read<ClrRuntimeInfo>(runtimeInfo);
                if (info.IsValid)
                {
                    if (dt.DataReader.TargetPlatform == OSPlatform.Windows)
                    {
                        IndexTimeStamp = info.RuntimePEProperties.TimeStamp;
                        IndexFileSize = info.RuntimePEProperties.FileSize;

                        if (dacTargetPlatform is not null)
                        {
                            var dacProp = info.DacPEProperties;
                            if (dacProp.TimeStamp != 0 && dacProp.FileSize != 0)
                                artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, dacProp.FileSize, dacProp.TimeStamp));
                        }

                        if (dbiTargetPlatform is not null)
                        {
                            var dbiProp = info.DbiPEProperties;
                            if (dbiProp.TimeStamp != 0 && dbiProp.FileSize != 0)
                                artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dbi, dbiTargetPlatform, targetArch, dbiProp.FileSize, dbiProp.TimeStamp));
                        }
                    }
                    else
                    {
                        BuildId = info.RuntimeBuildId;

                        if (dacTargetPlatform is not null)
                        {
                            var dacBuild = info.DacBuildId;
                            if (!dacBuild.IsDefaultOrEmpty)
                                artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, targetPlatform, dacBuild));
                        }

                        if (dbiTargetPlatform is not null)
                        {
                            var dbiBuild = info.DbiBuildId;
                            if (!dbiBuild.IsDefaultOrEmpty)
                                artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dbi, dbiTargetPlatform, targetArch, targetPlatform, dbiBuild));
                        }
                    }
                }

                // We don't actually know the version of single file CLR since the version of the module would be the user's app version
                Version = new Version();
            }
            else
            {
                artifacts = new List<DebugLibraryInfo>();

                IndexTimeStamp = module.IndexTimeStamp;
                IndexFileSize = module.IndexFileSize;
                BuildId = module.BuildId;
                Version = module.Version;
            }

            // Long-name dac
            if (dt.DataReader.TargetPlatform == OSPlatform.Windows && Version.Major != 0)
                artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, GetWindowsLongNameDac(flavor, currentArch, dt.DataReader.Architecture, Version), currentArch, IndexFileSize, IndexTimeStamp));


            // Short-name dac under CLR's properties
            if (targetPlatform == currentPlatform)
            {
                // We are debugging the process on the same operating system.
                bool foundLocalDac = false;
                if (dacTargetPlatform is not null)
                {
                    // Check if the user has the same CLR installed locally, and if so 
                    string? directory = Path.GetDirectoryName(module.FileName);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        string potentialClr = Path.Combine(directory, Path.GetFileName(module.FileName));
                        if (File.Exists(potentialClr))
                        {
                            try
                            {
                                using PEImage peimage = new PEImage(File.OpenRead(potentialClr));
                                if (peimage.IndexFileSize == IndexFileSize && peimage.IndexTimeStamp == IndexTimeStamp)
                                {
                                    string dacFound = Path.Combine(directory, dacTargetPlatform);
                                    if (File.Exists(dacFound))
                                    {
                                        dacCurrentPlatform = dacFound;
                                        foundLocalDac = true;
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (IndexFileSize != 0 && IndexTimeStamp != 0)
                    {
                        if (foundLocalDac)
                            artifacts.Insert(0, new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, IndexFileSize, IndexTimeStamp));
                        else
                            artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, IndexFileSize, IndexTimeStamp));
                    }

                    if (!BuildId.IsDefaultOrEmpty)
                    {
                        if (foundLocalDac)
                            artifacts.Insert(0, new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, targetPlatform, BuildId));
                        else
                            artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, targetPlatform, BuildId));
                    }
                }
            }
            else
            {
                // We are debugging the process on a different operating system.
                if (IndexFileSize != 0 && IndexTimeStamp != 0)
                {
                    // We currently only support cross-os debugging on windows targeting linux or os x runtimes.  So if we have windows properties,
                    // then we only generate one artifact (the target one).
                    if (dacTargetPlatform is not null)
                        artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, IndexFileSize, IndexTimeStamp));
                }

                if (!BuildId.IsDefaultOrEmpty)
                {
                    if (dacTargetPlatform is not null)
                        artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, targetPlatform, BuildId));

                    // If we are running from Windows, we can target Linux and OS X dumps.  Note that we still maintain targetArch and not
                    // currentArch in this scenario.  We do not build cross-os, cross-architecture debug libraries.
                    if (currentPlatform == OSPlatform.Windows && dacCurrentPlatform is not null)
                        artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacCurrentPlatform, targetArch, currentPlatform, BuildId));
                }
            }

            // Windows CLRDEBUGINFO resource
            CLR_DEBUG_RESOURCE resource = module.ReadResource<CLR_DEBUG_RESOURCE>("RCData", "CLRDEBUGINFO");
            if (resource.dwVersion == 0)
            {
                if (dacTargetPlatform is not null && resource.dwDacTimeStamp != 0 && resource.dwDacSizeOfImage != 0)
                    artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dac, dacTargetPlatform, targetArch, resource.dwDacSizeOfImage, resource.dwDacTimeStamp));

                if (dbiTargetPlatform is not null && resource.dwDbiTimeStamp != 0 && resource.dwDbiSizeOfImage != 0)
                    artifacts.Add(new DebugLibraryInfo(DebugLibraryKind.Dbi, dbiTargetPlatform, targetArch, resource.dwDbiSizeOfImage, resource.dwDbiTimeStamp));
            }

            DebuggingLibraries = EnumerateUnique(artifacts).ToImmutableArray();
        }

        private IEnumerable<DebugLibraryInfo> EnumerateUnique(List<DebugLibraryInfo> artifacts)
        {
            HashSet<DebugLibraryInfo> seen = new HashSet<DebugLibraryInfo>();

            foreach (DebugLibraryInfo library in artifacts)
                if (seen.Add(library))
                    yield return library;
        }

        private static string GetWindowsLongNameDac(ClrFlavor flavor, Architecture currentArchitecture, Architecture targetArchitecture, Version version)
        {
            var dacNameBase = flavor == ClrFlavor.Core ? c_coreDacFileNameBase : c_desktopDacFileNameBase;
            return $"{dacNameBase}_{currentArchitecture}_{targetArchitecture}_{version.Major}.{version.Minor}.{version.Build}.{version.Revision:D2}.dll".ToLower();
        }

        internal static ClrInfo? TryCreate(DataTarget dataTarget!!, ModuleInfo module!!)
        {
            if (IsSupportedRuntime(module, out ClrFlavor flavor))
                return new ClrInfo(dataTarget, flavor, module, 0);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.GetExtension(module.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                ulong singleFileRuntimeInfo = module.GetSymbolAddress(ClrRuntimeInfo.SymbolValue);
                if (singleFileRuntimeInfo != 0)
                    return new ClrInfo(dataTarget, ClrFlavor.Core, module, singleFileRuntimeInfo);
            }

            return null;
        }

        private static string? GetDbiFileName(ClrFlavor flavor, OSPlatform targetPlatform)
        {
            if (flavor == ClrFlavor.Core)
            {
                if (targetPlatform == OSPlatform.Windows)
                    return c_windowsDbiFileName;
                else if (targetPlatform == OSPlatform.Linux)
                    return c_linuxCoreDbiFileName;
                else if (targetPlatform == OSPlatform.OSX)
                    return c_macOSCoreDbiFileName;
            }

            if (flavor == ClrFlavor.Desktop)
            {
                if (targetPlatform == OSPlatform.Windows)
                    return c_windowsDbiFileName;
            }

            return null;
        }

        private static string? GetDacFileName(ClrFlavor flavor, OSPlatform targetPlatform)
        {
            if (flavor == ClrFlavor.Core)
            {
                if (targetPlatform == OSPlatform.Windows)
                    return c_coreDacFileName;
                else if (targetPlatform == OSPlatform.Linux)
                    return c_linuxCoreDacFileName;
                else if (targetPlatform == OSPlatform.OSX)
                    return c_macOSCoreDacFileName;
            }

            if (flavor == ClrFlavor.Desktop)
            {
                if (targetPlatform == OSPlatform.Windows)
                    return c_desktopDacFileName;
            }

            return null;
        }

        private static bool IsSupportedRuntime(ModuleInfo module, out ClrFlavor flavor)
        {
            flavor = default;

            string moduleName = Path.GetFileName(module.FileName);
            if (moduleName.Equals(c_desktopModuleName, StringComparison.OrdinalIgnoreCase))
            {
                flavor = ClrFlavor.Desktop;
                return true;
            }

            if (moduleName.Equals(c_coreModuleName, StringComparison.OrdinalIgnoreCase))
            {
                flavor = ClrFlavor.Core;
                return true;
            }

            if (moduleName.Equals(c_macOSCoreModuleName, StringComparison.OrdinalIgnoreCase))
            {
                flavor = ClrFlavor.Core;
                return true;
            }

            if (moduleName.Equals(c_linuxCoreModuleName, StringComparison.Ordinal))
            {
                flavor = ClrFlavor.Core;
                return true;
            }

            return false;
        }

        public DataTarget DataTarget { get; }

        /// <summary>
        /// Gets the version number of this runtime.
        /// </summary>
        public Version Version { get; }

        /// <summary>
        /// Returns whether this CLR was built as a single file executable.
        /// </summary>
        public bool IsSingleFile { get; }

        /// <summary>
        /// Gets the type of CLR this module represents.
        /// </summary>
        public ClrFlavor Flavor { get; }

        /// <summary>
        /// A list of debugging libraries associated associated with this .Net runtime.
        /// This can contain both the dac (used by ClrMD) and the DBI (not used by ClrMD).
        /// </summary>
        public ImmutableArray<DebugLibraryInfo> DebuggingLibraries { get; }

        /// <summary>
        /// Gets module information about the ClrInstance.
        /// </summary>
        public ModuleInfo ModuleInfo { get; }

        /// <summary>
        /// The timestamp under which this CLR is is archived (0 if this module is indexed under
        /// a BuildId instead).  Note that this may be a different value from ModuleInfo.IndexTimeStamp.
        /// In a single-file scenario, the ModuleInfo will be the info of the program's main executable
        /// and not CLR's properties.
        /// </summary>
        public int IndexTimeStamp { get; }

        /// <summary>
        /// The filesize under which this CLR is is archived (0 if this module is indexed under
        /// a BuildId instead).  Note that this may be a different value from ModuleInfo.IndexFileSize.
        /// In a single-file scenario, the ModuleInfo will be the info of the program's main executable
        /// and not CLR's properties.
        /// </summary>
        public int IndexFileSize { get; }

        /// <summary>
        /// The BuildId under which this CLR is archived.  BuildId.IsEmptyOrDefault will be true if
        /// this runtime is archived under file/timesize instead.
        /// </summary>
        public ImmutableArray<byte> BuildId { get; } = ImmutableArray<byte>.Empty;

        /// <summary>
        /// To string.
        /// </summary>
        /// <returns>A version string for this CLR.</returns>
        public override string ToString() => Version.ToString();

        /// <summary>
        /// Creates a runtime from the given DAC file on disk.
        /// </summary>
        /// <param name="dacPath">A full path to the matching DAC dll for this process.</param>
        /// <param name="ignoreMismatch">Whether or not to ignore mismatches between. </param>
        /// <returns></returns>
        public ClrRuntime CreateRuntime(string dacPath, bool ignoreMismatch = false)
        {
            if (string.IsNullOrEmpty(dacPath))
                throw new ArgumentNullException(nameof(dacPath));

            if (!File.Exists(dacPath))
                throw new FileNotFoundException(dacPath);

            if (!ignoreMismatch && !IsSingleFile)
            {
                DataTarget.PlatformFunctions.GetFileVersion(dacPath, out int major, out int minor, out int revision, out int patch);
                if (major != Version.Major || minor != Version.Minor || revision != Version.Build || patch != Version.Revision)
                    throw new InvalidOperationException($"Mismatched dac. Dac version: {major}.{minor}.{revision}.{patch}, expected: {Version}.");
            }

            return ConstructRuntime(dacPath);
        }

        public ClrRuntime CreateRuntime()
        {
            if (IntPtr.Size != DataTarget.DataReader.PointerSize)
                throw new InvalidOperationException("Mismatched pointer size between this process and the dac.");

            OSPlatform currentPlatform = GetCurrentPlatform();
            Architecture currentArch = RuntimeInformation.ProcessArchitecture;

            string? dacPath = null;
            bool foundOne = false;
            Exception? exception = null;

            IFileLocator? locator = DataTarget.FileLocator;

            foreach (DebugLibraryInfo dac in DebuggingLibraries.Where(r => r.Kind == DebugLibraryKind.Dac && r.Platform == currentPlatform && r.TargetArchitecture == currentArch))
            {
                foundOne = true;

                // If we have a full path, use it.  We already validated that the CLR matches.
                if (Path.GetFileName(dac.FileName) != dac.FileName)
                {
                    dacPath = dac.FileName;
                }
                else
                {
                    // The properties we are requesting under may not be the actual file properties, so don't request them.

                    if (locator != null)
                    {
                        if (!dac.IndexBuildId.IsDefaultOrEmpty)
                        {
                            if (dac.Platform == OSPlatform.Windows)
                                dacPath = locator.FindPEImage(dac.FileName, SymbolProperties.Coreclr, dac.IndexBuildId, DataTarget.DataReader.TargetPlatform, checkProperties: false);
                            else if (dac.Platform == OSPlatform.Linux)
                                dacPath = locator.FindElfImage(dac.FileName, SymbolProperties.Coreclr, dac.IndexBuildId, checkProperties: false);
                            else if (dac.Platform == OSPlatform.OSX)
                                dacPath = locator.FindMachOImage(dac.FileName, SymbolProperties.Coreclr, dac.IndexBuildId, checkProperties: false);
                        }
                        else if (dac.IndexTimeStamp != 0 && dac.IndexFileSize != 0)
                        {
                            if (dac.Platform == OSPlatform.Windows)
                                dacPath = DataTarget.FileLocator?.FindPEImage(dac.FileName, dac.IndexTimeStamp, dac.IndexFileSize, checkProperties: false);
                        }
                    }
                }

                if (dacPath is not null && File.Exists(dacPath))
                {
                    try
                    {
                        return CreateRuntime(dacPath, ignoreMismatch: true);
                    }
                    catch (Exception ex)
                    {
                        if (exception is null)
                            exception = ex;

                        dacPath = null;
                    }
                }
            }

            if (exception is not null)
                throw exception;

            // We should have had at least one dac enumerated if this is a supported scenario.
            if (!foundOne)
                ThrowCrossDebugError(currentPlatform);

            throw new FileNotFoundException("Could not find matching DAC for this runtime.");
        }

        private static OSPlatform GetCurrentPlatform()
        {
            OSPlatform currentPlatform;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                currentPlatform = OSPlatform.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                currentPlatform = OSPlatform.Linux;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                currentPlatform = OSPlatform.OSX;
            else
                throw new PlatformNotSupportedException();
            return currentPlatform;
        }

        private void ThrowCrossDebugError(OSPlatform current)
        {
            throw new InvalidOperationException($"Debugging a '{DataTarget.DataReader.TargetPlatform}' crash is not supported on '{current}'.");
        }

#pragma warning disable CA2000 // Dispose objects before losing scope
        private ClrRuntime ConstructRuntime(string dac)
        {
            if (IntPtr.Size != DataTarget.DataReader.PointerSize)
                throw new InvalidOperationException("Mismatched pointer size between this process and the dac.");

            DacLibrary dacLibrary = new DacLibrary(DataTarget, dac, ModuleInfo.ImageBase);
            DacInterface.SOSDac? sos = dacLibrary.SOSDacInterface;
            if (sos is null)
                throw new InvalidOperationException($"Could not create a ISOSDac pointer from this dac library: {dac}");

            var factory = new RuntimeBuilder(this, dacLibrary, sos);
            if (Flavor == ClrFlavor.Core)
                return factory.GetOrCreateRuntime();

            if (Version.Major < 4 || (Version.Major == 4 && Version.Minor == 5 && Version.Revision < 10000))
                throw new NotSupportedException($"CLR version '{Version}' is not supported by ClrMD.  For Desktop CLR, only CLR 4.6 and beyond are supported.");

            return factory.GetOrCreateRuntime();
        }

        private static T Read<T>(IDataReader reader, ref ulong address)
            where T : unmanaged
        {
            T t = reader.Read<T>(address);
            address += (uint)Marshal.SizeOf<T>();
            return t;
        }

        private static int Read(IDataReader reader, ref ulong address, Span<byte> buffer)
        {
            int read = reader.Read(address, buffer);
            address += (uint)read;
            return read;
        }


        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CLR_DEBUG_RESOURCE
        {
            public uint dwVersion;
            public Guid signature;
            public int dwDacTimeStamp;
            public int dwDacSizeOfImage;
            public int dwDbiTimeStamp;
            public int dwDbiSizeOfImage;
        }
    }
}
