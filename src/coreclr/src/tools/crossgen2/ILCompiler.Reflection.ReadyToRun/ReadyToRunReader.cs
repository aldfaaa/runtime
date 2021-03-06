// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Internal.CorConstants;
using Internal.Runtime;
using Internal.ReadyToRunConstants;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// based on <a href="https://github.com/dotnet/coreclr/blob/master/src/inc/pedecoder.h">src/inc/pedecoder.h</a> IMAGE_FILE_MACHINE_NATIVE_OS_OVERRIDE
    /// </summary>
    public enum OperatingSystem
    {
        Apple = 0x4644,
        FreeBSD = 0xADC4,
        Linux = 0x7B79,
        NetBSD = 0x1993,
        Windows = 0,
        Unknown = -1
    }

    public struct InstanceMethod
    {
        public byte Bucket;
        public ReadyToRunMethod Method;

        public InstanceMethod(byte bucket, ReadyToRunMethod method)
        {
            Bucket = bucket;
            Method = method;
        }
    }

    public sealed class ReadyToRunReader
    {
        private const string SystemModuleName = "System.Private.CoreLib";

        /// <summary>
        /// MetadataReader for the system module (normally System.Private.CoreLib)
        /// </summary>
        private MetadataReader _systemModuleReader;

        private readonly IAssemblyResolver _assemblyResolver;

        /// <summary>
        /// Reference assembly cache indexed by module indices as used in signatures
        /// </summary>
        private List<MetadataReader> _assemblyCache;

        /// <summary>
        /// Assembly headers for composite R2R images
        /// </summary>
        private List<ReadyToRunCoreHeader> _assemblyHeaders;

        // Header
        private OperatingSystem _operatingSystem;
        private Machine _machine;
        private Architecture _architecture;
        private bool _composite;
        private ulong _imageBase;
        private int _readyToRunHeaderRVA;
        private ReadyToRunHeader _readyToRunHeader;
        private List<ReadyToRunCoreHeader> _readyToRunAssemblyHeaders;

        // DebugInfo
        private Dictionary<int, DebugInfo> _runtimeFunctionToDebugInfo;

        // ManifestReferences
        private MetadataReader _manifestReader;
        private List<AssemblyReferenceHandle> _manifestReferences;

        // ExceptionInfo
        private Dictionary<int, EHInfo> _runtimeFunctionToEHInfo;

        /// <summary>
        /// Underlying PE image reader is used to access raw PE structures like header
        /// or section list.
        /// </summary>
        public PEReader PEReader { get; private set; }

        /// <summary>
        /// Byte array containing the ReadyToRun image
        /// </summary>
        public byte[] Image { get; private set; }

        /// <summary>
        /// Name of the image file
        /// </summary>
        public string Filename { get; private set; }

        /// <summary>
        /// Extra reference assemblies parsed from the manifest metadata.
        /// Only used by R2R assemblies with larger version bubble.
        /// The manifest contains extra assembly references created by resolved
        /// inlines and facades (non-existent in the source MSIL).
        /// In module overrides, these assembly references are represented
        /// by indices larger than the number of AssemblyRef rows in MetadataReader.
        /// The list originates in the top-level R2R image and is copied
        /// to all reference assemblies for the sake of simplicity.
        /// </summary>
        public IEnumerable<string> ManifestReferenceAssemblies
        {
            get
            {
                // TODO (refactoring) make this a IReadOnlyList<string> to be consistent with the rest of the interface
                foreach (AssemblyReferenceHandle manifestReference in ManifestReferences)
                {
                    yield return ManifestReader.GetString(ManifestReader.GetAssemblyReference(manifestReference).Name);
                }
            }
        }

        /// <summary>
        /// The type of target machine
        /// </summary>
        public Machine Machine
        {
            get
            {
                EnsureHeader();
                return _machine;
            }
        }

        /// <summary>
        /// Targeting operating system for the R2R executable
        /// </summary>
        public OperatingSystem OperatingSystem
        {
            get
            {
                EnsureHeader();
                return _operatingSystem;
            }
        }

        /// <summary>
        /// Targeting processor architecture of the R2R executable
        /// </summary>
        public Architecture Architecture
        {
            get
            {
                EnsureHeader();
                return _architecture;
            }
        }

        /// <summary>
        /// Return true when the executable is a composite R2R image.
        /// </summary>
        public bool Composite
        {
            get
            {
                EnsureHeader();
                return _composite;
            }
        }

        /// <summary>
        /// The preferred address of the first byte of image when loaded into memory;
        /// must be a multiple of 64K.
        /// </summary>
        public ulong ImageBase
        {
            get
            {
                EnsureHeader();
                return _imageBase;
            }
        }

        /// <summary>
        /// The ReadyToRun header
        /// </summary>
        public ReadyToRunHeader ReadyToRunHeader
        {
            get
            {
                EnsureHeader();
                return _readyToRunHeader;
            }
        }

        public IList<ReadyToRunCoreHeader> ReaderToRunAssemblyHeaders
        {
            get
            {
                EnsureHeader();
                return _readyToRunAssemblyHeaders;
            }
        }

        /// <summary>
        /// The runtime functions and method signatures of each method
        /// </summary>
        public IList<ReadyToRunMethod> Methods { get; private set; }

        /// <summary>
        /// Parsed instance entrypoint table entries.
        /// </summary>
        public IList<InstanceMethod> InstanceMethods { get; private set; }

        /// <summary>
        /// The available types from READYTORUN_SECTION_AVAILABLE_TYPES
        /// </summary>
        public IList<string> AvailableTypes { get; private set; }

        /// <summary>
        /// The compiler identifier string from READYTORUN_SECTION_COMPILER_IDENTIFIER
        /// </summary>
        public string CompilerIdentifier { get; private set; }

        /// <summary>
        /// List of import sections present in the R2R executable.
        /// </summary>
        public IList<ReadyToRunImportSection> ImportSections { get; private set; }

        /// <summary>
        /// Map from import cell addresses to their symbolic names.
        /// </summary>
        public Dictionary<int, string> ImportCellNames { get; private set; }

        internal Dictionary<int, DebugInfo> RuntimeFunctionToDebugInfo
        {
            get
            {
                EnsureDebugInfo();
                return _runtimeFunctionToDebugInfo;
            }
        }

        internal Dictionary<int, EHInfo> RuntimeFunctionToEHInfo
        {
            get
            {
                EnsureExceptionInfo();
                return _runtimeFunctionToEHInfo;
            }
        }

        internal List<AssemblyReferenceHandle> ManifestReferences
        {
            get
            {
                EnsureManifestReferences();
                return _manifestReferences;
            }
        }

        internal MetadataReader ManifestReader
        {
            get
            {
                EnsureManifestReferences();
                return _manifestReader;
            }
        }

        /// <summary>
        /// Initializes the fields of the R2RHeader and R2RMethods
        /// </summary>
        /// <param name="filename">PE image</param>
        /// <exception cref="BadImageFormatException">The Cor header flag must be ILLibrary</exception>
        public ReadyToRunReader(IAssemblyResolver assemblyResolver, MetadataReader metadata, PEReader peReader, string filename)
        {
            _assemblyResolver = assemblyResolver;
            PEReader = peReader;
            Filename = filename;
            Initialize(metadata);
        }

        /// <summary>
        /// Initializes the fields of the R2RHeader and R2RMethods
        /// </summary>
        /// <param name="filename">PE image</param>
        /// <exception cref="BadImageFormatException">The Cor header flag must be ILLibrary</exception>
        public unsafe ReadyToRunReader(IAssemblyResolver assemblyResolver, string filename)
        {
            _assemblyResolver = assemblyResolver;
            Filename = filename;
            Initialize(metadata: null);
        }

        private unsafe void Initialize(MetadataReader metadata)
        {
            _assemblyCache = new List<MetadataReader>();
            _assemblyHeaders = new List<ReadyToRunCoreHeader>();

            if (PEReader == null)
            {
                byte[] image = File.ReadAllBytes(Filename);
                Image = image;

                PEReader = new PEReader(Unsafe.As<byte[], ImmutableArray<byte>>(ref image));
            }

            if (metadata == null && PEReader.HasMetadata)
            {
                metadata = PEReader.GetMetadataReader();
            }

            if (metadata != null)
            {
                if ((PEReader.PEHeaders.CorHeader.Flags & CorFlags.ILLibrary) == 0)
                {
                    throw new BadImageFormatException("The file is not a ReadyToRun image");
                }

                _assemblyCache.Add(metadata);

                DirectoryEntry r2rHeaderDirectory = PEReader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory;
                _readyToRunHeaderRVA = r2rHeaderDirectory.RelativeVirtualAddress;
            }
            else if (!TryLocateNativeReadyToRunHeader())
            {
                throw new BadImageFormatException($"ECMA metadata / RTR_HEADER not found in file '{Filename}'");
            }

            ImmutableArray<byte> content = PEReader.GetEntireImage().GetContent();
            Image = Unsafe.As<ImmutableArray<byte>, byte[]>(ref content);

            if (_composite)
            {
                ParseComponentAssemblies();
            }

            // This is a work in progress toward lazy initialization.
            // Ideally, here should be the end of the Initialize() method

            ImportSections = new List<ReadyToRunImportSection>();
            ImportCellNames = new Dictionary<int, string>();
            ParseImportSections();

            Methods = new List<ReadyToRunMethod>();
            InstanceMethods = new List<InstanceMethod>();

            if (ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.RuntimeFunctions, out ReadyToRunSection runtimeFunctionSection))
            {
                int runtimeFunctionSize = CalculateRuntimeFunctionSize();
                uint nRuntimeFunctions = (uint)(runtimeFunctionSection.Size / runtimeFunctionSize);
                int runtimeFunctionOffset = GetOffset(runtimeFunctionSection.RelativeVirtualAddress);
                bool[] isEntryPoint = new bool[nRuntimeFunctions];

                // initialize R2RMethods
                ParseMethodDefEntrypoints(isEntryPoint);
                ParseInstanceMethodEntrypoints(isEntryPoint);
                ParseRuntimeFunctions(isEntryPoint, runtimeFunctionOffset);
            }

            AvailableTypes = new List<string>();
            ParseAvailableTypes();

            CompilerIdentifier = ParseCompilerIdentifier();
        }

        private bool TryLocateNativeReadyToRunHeader()
        {
            PEExportTable exportTable = PEReader.GetExportTable();
            if (exportTable.TryGetValue("RTR_HEADER", out _readyToRunHeaderRVA))
            {
                _composite = true;
                return true;
            }
            return false;
        }

        private MetadataReader GetSystemModuleMetadataReader()
        {
            if (_systemModuleReader == null)
            {
                if (_assemblyResolver != null)
                {
                    _systemModuleReader = _assemblyResolver.FindAssembly(SystemModuleName, Filename);
                }
            }
            return _systemModuleReader;
        }

        public MetadataReader GetGlobalMetadataReader()
        {
            EnsureHeader();
            return (_composite ? null : _assemblyCache[0]);
        }

        private unsafe void EnsureHeader()
        {
            if (_readyToRunHeader != null)
            {
                return;
            }
            uint machine = (uint)PEReader.PEHeaders.CoffHeader.Machine;
            _operatingSystem = OperatingSystem.Unknown;
            foreach (OperatingSystem os in Enum.GetValues(typeof(OperatingSystem)))
            {
                _machine = (Machine)(machine ^ (uint)os);
                if (Enum.IsDefined(typeof(Machine), _machine))
                {
                    _operatingSystem = os;
                    break;
                }
            }
            if (_operatingSystem == OperatingSystem.Unknown)
            {
                throw new BadImageFormatException($"Invalid Machine: {machine}");
            }

            switch (_machine)
            {
                case Machine.I386:
                    _architecture = Architecture.X86;
                    break;

                case Machine.Amd64:
                    _architecture = Architecture.X64;
                    break;

                case Machine.Arm:
                case Machine.Thumb:
                case Machine.ArmThumb2:
                    _architecture = Architecture.Arm;
                    break;

                case Machine.Arm64:
                    _architecture = Architecture.Arm64;
                    break;

                default:
                    throw new NotImplementedException(Machine.ToString());
            }


            _imageBase = PEReader.PEHeaders.PEHeader.ImageBase;

            // Initialize R2RHeader
            Debug.Assert(_readyToRunHeaderRVA != 0);
            int r2rHeaderOffset = GetOffset(_readyToRunHeaderRVA);
            _readyToRunHeader = new ReadyToRunHeader(Image, _readyToRunHeaderRVA, r2rHeaderOffset);
        }

        private void EnsureDebugInfo()
        {
            if (_runtimeFunctionToDebugInfo != null)
            {
                return;
            }
            _runtimeFunctionToDebugInfo = new Dictionary<int, DebugInfo>();
            if (!ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.DebugInfo, out ReadyToRunSection debugInfoSection))
            {
                return;
            }

            int debugInfoSectionOffset = GetOffset(debugInfoSection.RelativeVirtualAddress);

            NativeArray debugInfoArray = new NativeArray(Image, (uint)debugInfoSectionOffset);
            for (uint i = 0; i < debugInfoArray.GetCount(); ++i)
            {
                int offset = 0;
                if (!debugInfoArray.TryGetAt(Image, i, ref offset))
                {
                    continue;
                }

                var debugInfo = new DebugInfo(this, offset);
                _runtimeFunctionToDebugInfo.Add((int)i, debugInfo);
            }
        }

        private unsafe void EnsureManifestReferences()
        {
            if (_manifestReferences != null)
            {
                return;
            }
            _manifestReferences = new List<AssemblyReferenceHandle>();
            if (ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.ManifestMetadata, out ReadyToRunSection manifestMetadata))
            {
                fixed (byte* image = Image)
                {
                    _manifestReader = new MetadataReader(image + GetOffset(manifestMetadata.RelativeVirtualAddress), manifestMetadata.Size);
                    int assemblyRefCount = _manifestReader.GetTableRowCount(TableIndex.AssemblyRef);
                    for (int assemblyRefIndex = 1; assemblyRefIndex <= assemblyRefCount; assemblyRefIndex++)
                    {
                        AssemblyReferenceHandle asmRefHandle = MetadataTokens.AssemblyReferenceHandle(assemblyRefIndex);
                        _manifestReferences.Add(asmRefHandle);
                    }
                }
            }
        }

        private unsafe void EnsureExceptionInfo()
        {
            if (_runtimeFunctionToEHInfo != null)
            {
                return;
            }
            _runtimeFunctionToEHInfo = new Dictionary<int, EHInfo>();
            if (ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.ExceptionInfo, out ReadyToRunSection exceptionInfoSection))
            {
                int offset = GetOffset(exceptionInfoSection.RelativeVirtualAddress);
                int length = exceptionInfoSection.Size;
                int methodRva = BitConverter.ToInt32(Image, offset);
                int ehInfoRva = BitConverter.ToInt32(Image, offset + sizeof(uint));
                while ((length -= 2 * sizeof(uint)) >= 8)
                {
                    offset += 2 * sizeof(uint);
                    int nextMethodRva = BitConverter.ToInt32(Image, offset);
                    int nextEhInfoRva = BitConverter.ToInt32(Image, offset + sizeof(uint));
                    _runtimeFunctionToEHInfo.Add(methodRva, new EHInfo(this, ehInfoRva, methodRva, GetOffset(ehInfoRva), (nextEhInfoRva - ehInfoRva) / EHClause.Length));
                    methodRva = nextMethodRva;
                    ehInfoRva = nextEhInfoRva;
                }
            }
        }

        public bool InputArchitectureSupported()
        {
            return Machine != Machine.ArmThumb2; // CoreDisTools often fails to decode when disassembling ARM images (see https://github.com/dotnet/coreclr/issues/19637)
        }

        // TODO: Fix R2RDump issue where an R2R image cannot be dissassembled with the x86 CoreDisTools
        // For the short term, we want to error out with a decent message explaining the unexpected error
        // Issue https://github.com/dotnet/coreclr/issues/19564
        public bool DisassemblerArchitectureSupported()
        {
            System.Runtime.InteropServices.Architecture val = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
            return val != System.Runtime.InteropServices.Architecture.X86;
        }

        /// <summary>
        /// Each runtime function entry has 3 fields for Amd64 machines (StartAddress, EndAddress, UnwindRVA), otherwise 2 fields (StartAddress, UnwindRVA)
        /// </summary>
        private int CalculateRuntimeFunctionSize()
        {
            if (Machine == Machine.Amd64)
            {
                return 3 * sizeof(int);
            }
            return 2 * sizeof(int);
        }

        /// <summary>
        /// Initialize non-generic R2RMethods with method signatures from MethodDefHandle, and runtime function indices from MethodDefEntryPoints
        /// </summary>
        private void ParseMethodDefEntrypoints(bool[] isEntryPoint)
        {
            ReadyToRunSection methodEntryPointSection;
            if (ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out methodEntryPointSection))
            {
                ParseMethodDefEntrypointsSection(methodEntryPointSection, GetGlobalMetadataReader(), isEntryPoint);
            }
            else if (_readyToRunAssemblyHeaders != null)
            {
                for (int assemblyIndex = 0; assemblyIndex < _readyToRunAssemblyHeaders.Count; assemblyIndex++)
                {
                    if (_readyToRunAssemblyHeaders[assemblyIndex].Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out methodEntryPointSection))
                    {
                        ParseMethodDefEntrypointsSection(methodEntryPointSection, OpenReferenceAssembly(assemblyIndex + 2), isEntryPoint);
                    }
                }
            }
        }

        /// <summary>
        /// Parse a single method def entrypoint section. For composite R2R images, this method is called multiple times
        /// are method entrypoints are stored separately for each component assembly of the composite R2R executable.
        /// </summary>
        /// <param name="section">Method entrypoint section to parse</param>
        /// <param name="metadataReader">ECMA metadata reader representing this method entrypoint section</param>
        /// <param name="isEntryPoint">Set to true for each runtime function index representing a method entrypoint</param>
        private void ParseMethodDefEntrypointsSection(ReadyToRunSection section, MetadataReader metadataReader, bool[] isEntryPoint)
        {
            int methodDefEntryPointsOffset = GetOffset(section.RelativeVirtualAddress);
            NativeArray methodEntryPoints = new NativeArray(Image, (uint)methodDefEntryPointsOffset);
            uint nMethodEntryPoints = methodEntryPoints.GetCount();

            for (uint rid = 1; rid <= nMethodEntryPoints; rid++)
            {
                int offset = 0;
                if (methodEntryPoints.TryGetAt(Image, rid - 1, ref offset))
                {
                    EntityHandle methodHandle = MetadataTokens.MethodDefinitionHandle((int)rid);
                    int runtimeFunctionId;
                    int? fixupOffset;
                    GetRuntimeFunctionIndexFromOffset(offset, out runtimeFunctionId, out fixupOffset);
                    ReadyToRunMethod method = new ReadyToRunMethod(this, Methods.Count, metadataReader, methodHandle, runtimeFunctionId, owningType: null, constrainedType: null, instanceArgs: null, fixupOffset: fixupOffset);

                    if (method.EntryPointRuntimeFunctionId < 0 || method.EntryPointRuntimeFunctionId >= isEntryPoint.Length)
                    {
                        throw new BadImageFormatException("EntryPointRuntimeFunctionId out of bounds");
                    }
                    isEntryPoint[method.EntryPointRuntimeFunctionId] = true;
                    Methods.Add(method);
                }
            }
        }

        /// <summary>
        /// Initialize generic method instances with argument types and runtime function indices from InstanceMethodEntrypoints
        /// </summary>
        private void ParseInstanceMethodEntrypoints(bool[] isEntryPoint)
        {
            if (!ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.InstanceMethodEntryPoints, out ReadyToRunSection instMethodEntryPointSection))
            {
                return;
            }
            int instMethodEntryPointsOffset = GetOffset(instMethodEntryPointSection.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(Image, (uint)instMethodEntryPointsOffset);
            NativeHashtable instMethodEntryPoints = new NativeHashtable(Image, parser, (uint)(instMethodEntryPointsOffset + instMethodEntryPointSection.Size));
            NativeHashtable.AllEntriesEnumerator allEntriesEnum = instMethodEntryPoints.EnumerateAllEntries();
            NativeParser curParser = allEntriesEnum.GetNext();
            while (!curParser.IsNull())
            {
                SignatureDecoder decoder = new SignatureDecoder(_assemblyResolver, this, (int)curParser.Offset);
                MetadataReader mdReader = _composite ? null : _assemblyCache[0];

                string owningType = null;

                uint methodFlags = decoder.ReadUInt();
                if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType) != 0)
                {
                    mdReader = decoder.GetMetadataReaderFromModuleOverride() ?? mdReader;
                    if (_composite)
                    {
                        // The only types that don't have module overrides on them in composite images are primitive types within the system module
                        mdReader = GetSystemModuleMetadataReader();
                    }
                    owningType = decoder.ReadTypeSignatureNoEmit();
                }
                if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_SlotInsteadOfToken) != 0)
                {
                    throw new NotImplementedException();
                }
                EntityHandle methodHandle;
                int rid = (int)decoder.ReadUInt();
                if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MemberRefToken) != 0)
                {
                    methodHandle = MetadataTokens.MemberReferenceHandle(rid);
                }
                else
                {
                    methodHandle = MetadataTokens.MethodDefinitionHandle(rid);
                }
                string[] methodTypeArgs = null;
                if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation) != 0)
                {
                    uint typeArgCount = decoder.ReadUInt();
                    methodTypeArgs = new string[typeArgCount];
                    for (int typeArgIndex = 0; typeArgIndex < typeArgCount; typeArgIndex++)
                    {
                        methodTypeArgs[typeArgIndex] = decoder.ReadTypeSignatureNoEmit();
                    }
                }

                string constrainedType = null;
                if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained) != 0)
                {
                    constrainedType = decoder.ReadTypeSignatureNoEmit();
                }

                int runtimeFunctionId;
                int? fixupOffset;
                GetRuntimeFunctionIndexFromOffset((int)decoder.Offset, out runtimeFunctionId, out fixupOffset);
                ReadyToRunMethod method = new ReadyToRunMethod(
                    this,
                    Methods.Count,
                    mdReader,
                    methodHandle,
                    runtimeFunctionId,
                    owningType,
                    constrainedType,
                    methodTypeArgs,
                    fixupOffset);
                if (method.EntryPointRuntimeFunctionId >= 0 && method.EntryPointRuntimeFunctionId < isEntryPoint.Length)
                {
                    isEntryPoint[method.EntryPointRuntimeFunctionId] = true;
                }
                Methods.Add(method);
                InstanceMethods.Add(new InstanceMethod(curParser.LowHashcode, method));
                curParser = allEntriesEnum.GetNext();
            }
        }

        /// <summary>
        /// Get the RVAs of the runtime functions for each method
        /// based on <a href="https://github.com/dotnet/coreclr/blob/master/src/zap/zapcode.cpp">ZapUnwindInfo::Save</a>
        /// </summary>
        private void ParseRuntimeFunctions(bool[] isEntryPoint, int runtimeFunctionOffset)
        {
            foreach (ReadyToRunMethod method in Methods)
            {
                int runtimeFunctionId = method.EntryPointRuntimeFunctionId;
                if (runtimeFunctionId == -1)
                    continue;
                int runtimeFunctionSize = CalculateRuntimeFunctionSize();
                ParseRuntimeFunctionsForMethod(isEntryPoint, runtimeFunctionOffset + runtimeFunctionId * runtimeFunctionSize, method, runtimeFunctionId);
            }
        }

        private void ParseRuntimeFunctionsForMethod(bool[] isEntryPoint, int curOffset, ReadyToRunMethod method, int runtimeFunctionId)
        {
            BaseGcInfo gcInfo = null;
            int codeOffset = 0;
            do
            {
                int startRva = NativeReader.ReadInt32(Image, ref curOffset);
                int endRva = -1;
                if (Machine == Machine.Amd64)
                {
                    endRva = NativeReader.ReadInt32(Image, ref curOffset);
                }
                int unwindRva = NativeReader.ReadInt32(Image, ref curOffset);
                int unwindOffset = GetOffset(unwindRva);

                BaseUnwindInfo unwindInfo = null;
                if (Machine == Machine.Amd64)
                {
                    unwindInfo = new Amd64.UnwindInfo(Image, unwindOffset);
                    if (isEntryPoint[runtimeFunctionId])
                    {
                        gcInfo = new Amd64.GcInfo(Image, unwindOffset + unwindInfo.Size, Machine, ReadyToRunHeader.MajorVersion);
                    }
                }
                else if (Machine == Machine.I386)
                {
                    unwindInfo = new x86.UnwindInfo(Image, unwindOffset);
                    if (isEntryPoint[runtimeFunctionId])
                    {
                        gcInfo = new x86.GcInfo(Image, unwindOffset, Machine, ReadyToRunHeader.MajorVersion);
                    }
                }
                else if (Machine == Machine.ArmThumb2)
                {
                    unwindInfo = new Arm.UnwindInfo(Image, unwindOffset);
                    if (isEntryPoint[runtimeFunctionId])
                    {
                        gcInfo = new Amd64.GcInfo(Image, unwindOffset + unwindInfo.Size, Machine, ReadyToRunHeader.MajorVersion); // Arm and Arm64 use the same GcInfo format as x64
                    }
                }
                else if (Machine == Machine.Arm64)
                {
                    unwindInfo = new Arm64.UnwindInfo(Image, unwindOffset);
                    if (isEntryPoint[runtimeFunctionId])
                    {
                        gcInfo = new Amd64.GcInfo(Image, unwindOffset + unwindInfo.Size, Machine, ReadyToRunHeader.MajorVersion);
                    }
                }

                RuntimeFunction rtf = new RuntimeFunction(
                    this,
                    runtimeFunctionId,
                    startRva,
                    endRva,
                    unwindRva,
                    codeOffset,
                    method,
                    unwindInfo,
                    gcInfo);

                method.RuntimeFunctions.Add(rtf);
                runtimeFunctionId++;
                codeOffset += rtf.Size;
            }
            while (runtimeFunctionId < isEntryPoint.Length && !isEntryPoint[runtimeFunctionId]);
        }

        /// <summary>
        /// Iterates through a native hashtable to get all RIDs
        /// </summary>
        private void ParseAvailableTypes()
        {
            ReadyToRunSection availableTypesSection;
            if (ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.AvailableTypes, out availableTypesSection))
            {
                ParseAvailableTypesSection(availableTypesSection, GetGlobalMetadataReader());
            }
            else if (_readyToRunAssemblyHeaders != null)
            {
                for (int assemblyIndex = 0; assemblyIndex < _readyToRunAssemblyHeaders.Count; assemblyIndex++)
                {
                    if (_readyToRunAssemblyHeaders[assemblyIndex].Sections.TryGetValue(
                        ReadyToRunSectionType.AvailableTypes, out availableTypesSection))
                    {
                        ParseAvailableTypesSection(availableTypesSection, OpenReferenceAssembly(assemblyIndex + 2));
                    }
                }
            }
        }

        /// <summary>
        /// Parse a single available types section. For composite R2R images this method is called multiple times
        /// as available types are stored separately for each component assembly of the composite R2R executable.
        /// </summary>
        /// <param name="availableTypesSection"></param>
        private void ParseAvailableTypesSection(ReadyToRunSection availableTypesSection, MetadataReader metadataReader)
        {
            int availableTypesOffset = GetOffset(availableTypesSection.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(Image, (uint)availableTypesOffset);
            NativeHashtable availableTypes = new NativeHashtable(Image, parser, (uint)(availableTypesOffset + availableTypesSection.Size));
            NativeHashtable.AllEntriesEnumerator allEntriesEnum = availableTypes.EnumerateAllEntries();
            NativeParser curParser = allEntriesEnum.GetNext();
            while (!curParser.IsNull())
            {
                uint rid = curParser.GetUnsigned();

                bool isExportedType = (rid & 1) != 0;
                rid = rid >> 1;

                if (isExportedType)
                {
                    ExportedTypeHandle exportedTypeHandle = MetadataTokens.ExportedTypeHandle((int)rid);
                    string exportedTypeName = GetExportedTypeFullName(metadataReader, exportedTypeHandle);
                    AvailableTypes.Add("exported " + exportedTypeName);
                }
                else
                {
                    TypeDefinitionHandle typeDefHandle = MetadataTokens.TypeDefinitionHandle((int)rid);
                    string typeDefName = MetadataNameFormatter.FormatHandle(metadataReader, typeDefHandle);
                    AvailableTypes.Add(typeDefName);
                }

                curParser = allEntriesEnum.GetNext();
            }
        }

        /// <summary>
        /// Converts the bytes in the compiler identifier section to characters in a string
        /// </summary>
        private string ParseCompilerIdentifier()
        {
            if (!ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.CompilerIdentifier, out ReadyToRunSection compilerIdentifierSection))
            {
                return "";
            }
            byte[] identifier = new byte[compilerIdentifierSection.Size - 1];
            int identifierOffset = GetOffset(compilerIdentifierSection.RelativeVirtualAddress);
            Array.Copy(Image, identifierOffset, identifier, 0, compilerIdentifierSection.Size - 1);
            return Encoding.UTF8.GetString(identifier);
        }

        /// <summary>
        /// Decode the ReadyToRun section READYTORUN_SECTION_ASSEMBLIES containing a list of per assembly R2R core headers
        /// for each assembly comprising the composite R2R executable.
        /// </summary>
        private void ParseComponentAssemblies()
        {
            ReadyToRunSection componentAssembliesSection;
            if (!ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.ComponentAssemblies, out componentAssembliesSection))
            {
                return;
            }

            _readyToRunAssemblyHeaders = new List<ReadyToRunCoreHeader>();

            int offset = GetOffset(componentAssembliesSection.RelativeVirtualAddress);
            int numberOfAssemblyHeaderRVAs = componentAssembliesSection.Size / ComponentAssembly.Size;

            for (int assemblyIndex = 0; assemblyIndex < numberOfAssemblyHeaderRVAs; assemblyIndex++)
            {
                ComponentAssembly assembly = new ComponentAssembly(Image, ref offset);
                int headerOffset = GetOffset(assembly.AssemblyHeaderRVA);

                ReadyToRunCoreHeader assemblyHeader = new ReadyToRunCoreHeader(Image, ref headerOffset);
                _readyToRunAssemblyHeaders.Add(assemblyHeader);
            }
        }

        /// <summary>
        /// based on <a href="https://github.com/dotnet/coreclr/blob/master/src/zap/zapimport.cpp">ZapImportSectionsTable::Save</a>
        /// </summary>
        private void ParseImportSections()
        {
            if (!ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.ImportSections, out ReadyToRunSection importSectionsSection))
            {
                return;
            }
            int offset = GetOffset(importSectionsSection.RelativeVirtualAddress);
            int endOffset = offset + importSectionsSection.Size;
            while (offset < endOffset)
            {
                int rva = NativeReader.ReadInt32(Image, ref offset);
                int sectionOffset = GetOffset(rva);
                int startOffset = sectionOffset;
                int size = NativeReader.ReadInt32(Image, ref offset);
                CorCompileImportFlags flags = (CorCompileImportFlags)NativeReader.ReadUInt16(Image, ref offset);
                byte type = NativeReader.ReadByte(Image, ref offset);
                byte entrySize = NativeReader.ReadByte(Image, ref offset);
                if (entrySize == 0)
                {
                    switch (Machine)
                    {
                        case Machine.I386:
                        case Machine.ArmThumb2:
                            entrySize = 4;
                            break;

                        case Machine.Amd64:
                        case Machine.Arm64:
                            entrySize = 8;
                            break;

                        default:
                            throw new NotImplementedException(Machine.ToString());
                    }
                }
                int entryCount = 0;
                if (entrySize != 0)
                {
                    entryCount = size / entrySize;
                }
                int signatureRVA = NativeReader.ReadInt32(Image, ref offset);

                int signatureOffset = 0;
                if (signatureRVA != 0)
                {
                    signatureOffset = GetOffset(signatureRVA);
                }
                List<ReadyToRunImportSection.ImportSectionEntry> entries = new List<ReadyToRunImportSection.ImportSectionEntry>();
                for (int i = 0; i < entryCount; i++)
                {
                    int entryOffset = sectionOffset - startOffset;
                    long section = NativeReader.ReadInt64(Image, ref sectionOffset);
                    uint sigRva = NativeReader.ReadUInt32(Image, ref signatureOffset);
                    int sigOffset = GetOffset((int)sigRva);
                    string cellName = MetadataNameFormatter.FormatSignature(_assemblyResolver, this, sigOffset);
                    entries.Add(new ReadyToRunImportSection.ImportSectionEntry(entries.Count, entryOffset, entryOffset + rva, section, sigRva, cellName));
                    ImportCellNames.Add(rva + entrySize * i, cellName);
                }

                int auxDataRVA = NativeReader.ReadInt32(Image, ref offset);
                int auxDataOffset = 0;
                if (auxDataRVA != 0)
                {
                    auxDataOffset = GetOffset(auxDataRVA);
                }
                ImportSections.Add(new ReadyToRunImportSection(ImportSections.Count, this, rva, size, flags, type, entrySize, signatureRVA, entries, auxDataRVA, auxDataOffset, Machine, ReadyToRunHeader.MajorVersion));
            }
        }

        /// <summary>
        /// Get the index in the image byte array corresponding to the RVA
        /// </summary>
        /// <param name="rva">The relative virtual address</param>
        public int GetOffset(int rva)
        {
            return PEReader.GetOffset(rva);
        }

        /// <summary>
        /// Get the full name of an ExportedType, including namespace
        /// </summary>
        private static string GetExportedTypeFullName(MetadataReader mdReader, ExportedTypeHandle handle)
        {
            string typeNamespace = "";
            string typeStr = "";
            try
            {
                ExportedType exportedType = mdReader.GetExportedType(handle);
                typeStr = "." + mdReader.GetString(exportedType.Name) + typeStr;
                typeNamespace = mdReader.GetString(exportedType.Namespace);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            return typeNamespace + typeStr;
        }

        /// <summary>
        /// Reads the method entrypoint from the offset. Used for non-generic methods
        /// based on <a href="https://github.com/dotnet/coreclr/blob/master/src/debug/daccess/nidump.cpp">NativeImageDumper::DumpReadyToRunMethods</a>
        /// </summary>
        private void GetRuntimeFunctionIndexFromOffset(int offset, out int runtimeFunctionIndex, out int? fixupOffset)
        {
            fixupOffset = null;

            // get the id of the entry point runtime function from the MethodEntryPoints NativeArray
            uint id = 0; // the RUNTIME_FUNCTIONS index
            offset = (int)NativeReader.DecodeUnsigned(Image, (uint)offset, ref id);
            if ((id & 1) != 0)
            {
                if ((id & 2) != 0)
                {
                    uint val = 0;
                    NativeReader.DecodeUnsigned(Image, (uint)offset, ref val);
                    offset -= (int)val;
                }

                fixupOffset = offset;

                id >>= 2;
            }
            else
            {
                id >>= 1;
            }

            runtimeFunctionIndex = (int)id;
        }

        private AssemblyReferenceHandle GetAssemblyAtIndex(int refAsmIndex, out MetadataReader metadataReader)
        {
            Debug.Assert(refAsmIndex != 0);

            int assemblyRefCount = (_composite ? 0 : _assemblyCache[0].GetTableRowCount(TableIndex.AssemblyRef));
            AssemblyReferenceHandle assemblyReferenceHandle;
            if (refAsmIndex <= assemblyRefCount)
            {
                metadataReader = _assemblyCache[0];
                assemblyReferenceHandle = MetadataTokens.AssemblyReferenceHandle(refAsmIndex);
            }
            else
            {
                metadataReader = ManifestReader;
                assemblyReferenceHandle = ManifestReferences[refAsmIndex - assemblyRefCount - 2];
            }

            return assemblyReferenceHandle;
        }

        internal string GetReferenceAssemblyName(int refAsmIndex)
        {
            AssemblyReferenceHandle handle = GetAssemblyAtIndex(refAsmIndex, out MetadataReader reader);
            return reader.GetString(reader.GetAssemblyReference(handle).Name);
        }

        /// <summary>
        /// Open a given reference assembly (relative to this ECMA metadata file).
        /// </summary>
        /// <param name="refAsmIndex">Reference assembly index</param>
        /// <returns>MetadataReader instance representing the reference assembly</returns>
        internal MetadataReader OpenReferenceAssembly(int refAsmIndex)
        {
            MetadataReader result = (refAsmIndex < _assemblyCache.Count ? _assemblyCache[refAsmIndex] : null);
            if (result == null)
            {
                AssemblyReferenceHandle assemblyReferenceHandle = GetAssemblyAtIndex(refAsmIndex, out MetadataReader metadataReader);

                result = _assemblyResolver.FindAssembly(metadataReader, assemblyReferenceHandle, Filename);
                if (result == null)
                {
                    string name = metadataReader.GetString(metadataReader.GetAssemblyReference(assemblyReferenceHandle).Name);
                    throw new Exception($"Missing reference assembly: {name}");
                }
                while (_assemblyCache.Count <= refAsmIndex)
                {
                    _assemblyCache.Add(null);
                }
                _assemblyCache[refAsmIndex] = result;
            }
            return result;
        }
    }
}
