﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Buffers.Tests
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using DotNetty.Common.Utilities;
    using Xunit;

    public class AbstractByteBufferTests
    {
        [Fact]
        public async Task WriteBytesAsyncPartialWrite()
        {
            const int CopyLength = 200 * 1024;
            const int SourceLength = 300 * 1024;
            const int BufferCapacity = 400 * 1024;

            var bytes = new byte[SourceLength];
            var random = new Random(Guid.NewGuid().GetHashCode());
            random.NextBytes(bytes);

            IByteBuffer buffer = Unpooled.Buffer(BufferCapacity);
            int read;
            using (var stream = new PortionedMemoryStream(bytes, Enumerable.Repeat(1, int.MaxValue).Select(_ => random.Next(1, 10240))))
            {
                read = await buffer.WriteBytesAsync(stream, CopyLength);
            }
            Assert.Equal(CopyLength, read);
            Assert.True(ByteBufferUtil.Equals(new UnpooledHeapByteBuffer(UnpooledByteBufferAllocator.Default, bytes.Slice(0, CopyLength), int.MaxValue), buffer));
        }
    }
}