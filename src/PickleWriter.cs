using System;
using System.IO;

namespace Ibasa.Pikala
{
    /// <summary>
    /// By default BinaryWriter will flush the base stream on every access to the BaseStream property.
    /// We don't need this behvaiour so have this overriden class to just remove that one behaviour.
    /// </summary>
    sealed class PickleWriter : BinaryWriter
    {
        public PickleWriter(Stream stream) : base(stream)
        {
        }

        public override Stream BaseStream => base.OutStream;
    }
}
