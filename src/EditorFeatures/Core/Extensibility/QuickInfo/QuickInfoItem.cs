﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Editor
{
    internal class QuickInfoItem
    {
        public TextSpan TextSpan { get; private set; }
        public IDeferredQuickInfoContent Content { get; private set; }

        public QuickInfoItem(TextSpan textSpan, IDeferredQuickInfoContent content)
        {
            this.TextSpan = textSpan;
            this.Content = content;
        }
    }
}
