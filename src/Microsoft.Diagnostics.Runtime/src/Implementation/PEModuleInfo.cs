﻿using Microsoft.Diagnostics.Runtime.Utilities;
using System;
using System.Linq;
using System.Threading;

namespace Microsoft.Diagnostics.Runtime.Implementation
{
    /// <summary>
    /// A helper to implement <see cref="ModuleInfo"/> for PEImages.
    /// </summary>
    internal class PEModuleInfo : ModuleInfo
    {
        private readonly IDataReader _dataReader;
        private readonly bool _isVirtual;

        private int _timestamp;
        private int _filesize;

        private bool _loaded;
        private PdbInfo? _pdb;
        private bool? _isManaged;
        private PEImage? _peImage;
        private System.Version? _version;

        internal PEImage? GetPEImage()
        {
            if (_peImage is not null || _loaded)
                return _peImage;

            try
            {
                PEImage image = new PEImage(new ReadVirtualStream(_dataReader, (long)ImageBase, int.MaxValue), leaveOpen: false, isVirtual: _isVirtual);
                if (!image.IsValid)
                {
                    image.Dispose();
                    image = new PEImage(new ReadVirtualStream(_dataReader, (long)ImageBase, int.MaxValue), leaveOpen: false, isVirtual: !_isVirtual);
                }

                if (image.IsValid)
                {
                    Interlocked.CompareExchange(ref _peImage, image, null);
                    _loaded = true;
                    return _peImage;
                }

                image.Dispose();
            }
            catch
            {
            }

            _loaded = true;
            return null;
        }

        /// <inheritdoc/>
        public override System.Version Version
        {
            get
            {
                if (_version is not null)
                    return _version;

                System.Version version = GetPEImage()?.GetFileVersionInfo()?.Version ?? new System.Version();
                _version = version;
                return version;
            }
        }

        /// <inheritdoc/>
        public override PdbInfo? Pdb
        {
            get
            {
                if (_pdb is not null)
                    return _pdb;

                PdbInfo? pdb = GetPEImage()?.DefaultPdb;
                _pdb = pdb;
                return pdb;
            }
        }

        /// <inheritdoc/>
        public override bool IsManaged
        {
            get
            {
                if (_isManaged is bool result)
                    return result;

                result = GetPEImage()?.IsManaged ?? false;
                _isManaged = result;
                return result;
            }
        }

        /// <inheritdoc/>
        public override int IndexFileSize
        {
            get
            {
                if (_timestamp == 0 && _filesize == 0)
                {
                    PEImage? img = GetPEImage();
                    if (img is not null)
                    {
                        _timestamp = img.IndexTimeStamp;
                        _filesize = img.IndexFileSize;
                    }
                }

                return _filesize;
            }
        }

        /// <inheritdoc/>
        public override int IndexTimeStamp
        {
            get
            {
                if (_timestamp == 0 && _filesize == 0)
                {
                    PEImage? img = GetPEImage();
                    if (img is not null)
                    {
                        _timestamp = img.IndexTimeStamp;
                        _filesize = img.IndexFileSize;
                    }
                }

                return _timestamp;
            }
        }

        public override ulong GetSymbolAddress(string symbol)
        {
            PEImage? img = GetPEImage();
            if (img is not null && img.TryGetExportSymbol(symbol, out ulong offset) && offset != 0)
                return ImageBase + offset;

            return 0;
        }

        internal override T ReadResource<T>(params string[] path)
        {
            PEImage? img = GetPEImage();
            if (img is not null)
            {
                ResourceEntry? node = img.Resources;
                
                foreach (string part in path)
                {
                    if (node is null)
                        break;

                    node = node[part];
                }

                node = node?.Children.FirstOrDefault();
                if (node is not null)
                    return node.GetData<T>();
            }

            return default;
        }

        public PEModuleInfo(IDataReader dataReader!!, ulong imageBase, string fileName!!, bool isVirtual)
            : base(imageBase, fileName)
        {
            _dataReader = dataReader;
            _isVirtual = isVirtual;
        }

        public PEModuleInfo(IDataReader dataReader, ulong imageBase, string fileName, bool isVirtual, int timestamp, int filesize, Version? version = null)
            : this(dataReader, imageBase, fileName, isVirtual)
        {
            _timestamp = timestamp;
            _filesize = filesize;
            _version = version;
        }
    }
}