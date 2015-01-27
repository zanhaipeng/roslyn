// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.RenameTracking
{
    /// <summary>
    /// Also known as "rename smart tag," this watches text changes in open buffers, determines
    /// whether they can be interpreted as an identifier rename, and if so displays a smart tag 
    /// that can perform a rename on that symbol. Each text buffer is tracked independently.
    /// </summary>
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(RenameTrackingTag))]
    [ContentType(ContentTypeNames.RoslynContentType)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed partial class RenameTrackingTaggerProvider : ITaggerProvider
    {
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        private readonly IAsynchronousOperationListener _asyncListener;
        private readonly IWaitIndicator _waitIndicator;
        private readonly IInlineRenameService _inlineRenameService;
        private readonly IEnumerable<IRefactorNotifyService> _refactorNotifyServices;

        [ImportingConstructor]
        public RenameTrackingTaggerProvider(
            ITextUndoHistoryRegistry undoHistoryRegistry,
            IWaitIndicator waitIndicator,
            IInlineRenameService inlineRenameService,
            [ImportMany] IEnumerable<IRefactorNotifyService> refactorNotifyServices,
            [ImportMany] IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>> asyncListeners)
        {
            _undoHistoryRegistry = undoHistoryRegistry;
            _waitIndicator = waitIndicator;
            _inlineRenameService = inlineRenameService;
            _refactorNotifyServices = refactorNotifyServices;
            _asyncListener = new AggregateAsynchronousOperationListener(asyncListeners, FeatureAttribute.RenameTracking);
        }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            var stateMachine = buffer.Properties.GetOrCreateSingletonProperty(() => new StateMachine(buffer, _inlineRenameService, _asyncListener));
            return new Tagger(stateMachine, _undoHistoryRegistry, _waitIndicator, _refactorNotifyServices) as ITagger<T>;
        }

        internal static void ResetRenameTrackingState(Workspace workspace, DocumentId documentId)
        {
            ResetRenameTrackingStateWorker(workspace, documentId, visible: false);
        }

        internal static bool ResetVisibleRenameTrackingState(Workspace workspace, DocumentId documentId)
        {
            return ResetRenameTrackingStateWorker(workspace, documentId, visible: true);
        }

        internal static bool ResetRenameTrackingStateWorker(Workspace workspace, DocumentId documentId, bool visible)
        {
            if (workspace.IsDocumentOpen(documentId))
            {
                var document = workspace.CurrentSolution.GetDocument(documentId);

                SourceText text;
                StateMachine stateMachine;
                ITextBuffer textBuffer;
                if (document != null &&
                    document.TryGetText(out text))
                {
                    textBuffer = text.Container.TryGetTextBuffer();
                    if (textBuffer == null)
                    {
                        Environment.FailFast(string.Format("document with name {0} is open but textBuffer is null. Textcontainer is of type {1}. SourceText is: {2}",
                                                            document.Name, text.Container.GetType().FullName, text.ToString()));
                    }

                    if (textBuffer.Properties.TryGetProperty(typeof(StateMachine), out stateMachine))
                    {
                        if (visible)
                        {
                            return stateMachine.ClearVisibleTrackingSession();
                        }
                        else
                        {
                            return stateMachine.ClearTrackingSession();
                        }
                    }
                }
            }

            return false;
        }

        internal static async Task<IEnumerable<Diagnostic>> GetDiagnosticsAsync(SyntaxTree tree, DiagnosticDescriptor diagnosticDescriptor, CancellationToken cancellationToken)
        {
            try
            {
                // This can run on a background thread.
                SourceText text;
                StateMachine stateMachine;
                if (tree != null &&
                    tree.TryGetText(out text))
                {
                    var textBuffer = text.Container.TryGetTextBuffer();
                    if (textBuffer != null && textBuffer.Properties.TryGetProperty(typeof(StateMachine), out stateMachine))
                    {
                        return await stateMachine.GetDiagnostic(tree, diagnosticDescriptor, cancellationToken).ConfigureAwait(false);
                    }
                }

                return SpecializedCollections.EmptyEnumerable<Diagnostic>();
            }
            catch (Exception e) when(FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
            }

        internal static CodeAction CreateCodeAction(
            Document document,
            Diagnostic diagnostic,
            IWaitIndicator waitIndicator,
            IEnumerable<IRefactorNotifyService> refactorNotifyServices,
            ITextUndoHistoryRegistry undoHistoryRegistry,
            bool showPreview)
        {
            // This can run on a background thread.
            string message = diagnostic.GetMessage();

            if (showPreview)
            {
                message += EditorFeaturesResources.WithPreview;
            }

            return new RenameTrackingCodeAction(document, message, refactorNotifyServices, undoHistoryRegistry, showPreview);
        }

        internal static bool IsRenamableIdentifier(Task<TriggerIdentifierKind> isRenamableIdentifierTask, bool waitForResult, CancellationToken cancellationToken)
        {
            if (isRenamableIdentifierTask.Status == TaskStatus.RanToCompletion && isRenamableIdentifierTask.Result != TriggerIdentifierKind.NotRenamable)
            {
                return true;
            }
            else if (isRenamableIdentifierTask.Status == TaskStatus.Canceled)
            {
                return false;
            }
            else if (waitForResult)
            {
                return WaitForIsRenamableIdentifier(isRenamableIdentifierTask, cancellationToken);
            }
            else
            {
                return false;
            }
        }

        internal static bool WaitForIsRenamableIdentifier(Task<TriggerIdentifierKind> isRenamableIdentifierTask, CancellationToken cancellationToken)
        {
            try
            {
                return isRenamableIdentifierTask.WaitAndGetResult(cancellationToken) != TriggerIdentifierKind.NotRenamable;
            }
            catch (AggregateException e) when(e.InnerException is OperationCanceledException)
            {
                // We passed in a different cancellationToken, so if there's a race and 
                // isRenamableIdentifierTask was cancelled, we'll get an AggregateException
                return false;
            }
            }
        }
    }
