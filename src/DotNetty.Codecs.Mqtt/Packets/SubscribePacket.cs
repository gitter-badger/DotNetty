﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Mqtt.Packets
{
    using System.Collections.Generic;

    public sealed class SubscribePacket : PacketWithId
    {
        public override PacketType PacketType
        {
            get { return PacketType.SUBSCRIBE; }
        }

        public override QualityOfService QualityOfService
        {
            get { return QualityOfService.AtLeastOnce; }
        }

        public IReadOnlyList<SubscriptionRequest> Requests { get; set; }
    }
}