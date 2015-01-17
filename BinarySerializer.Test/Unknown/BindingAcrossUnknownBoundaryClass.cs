﻿using BinarySerialization;

namespace BinarySerializer.Test.Unknown
{
    public class BindingAcrossUnknownBoundaryClass
    {
        [FieldOrder(0)]
        public byte SubfieldLength { get; set; }

        [FieldOrder(1)]
        public object Field { get; set; }
    }
}
