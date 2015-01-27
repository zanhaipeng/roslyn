// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.ReferenceHighlighting
{
    internal abstract class AbstractDocumentHighlightsService : IDocumentHighlightsService
    {
        public async Task<IEnumerable<DocumentHighlights>> GetDocumentHighlightsAsync(Document document, int position, IEnumerable<Document> documentsToSearch, CancellationToken cancellationToken)
        {
            // use speculative semantic model to see whether we are on a symbol we can do HR
            var span = new TextSpan(position, 0);
            var solution = document.Project.Solution;
            var semanticModel = await document.GetSemanticModelForSpanAsync(span, cancellationToken).ConfigureAwait(false);
            var symbol = SymbolFinder.FindSymbolAtPosition(semanticModel, position, solution.Workspace, cancellationToken: cancellationToken);
            if (symbol == null)
            {
                return SpecializedCollections.EmptyEnumerable<DocumentHighlights>();
            }

            symbol = await GetSymbolToSearchAsync(document, position, semanticModel, symbol, cancellationToken).ConfigureAwait(false);

            // Get unique tags for referenced symbols
            return await GetTagsForReferencedSymbolAsync(symbol, ImmutableHashSet.CreateRange(documentsToSearch), solution, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ISymbol> GetSymbolToSearchAsync(Document document, int position, SemanticModel semanticModel, ISymbol symbols, CancellationToken cancellationToken)
        {
            // see whether we can use symbols as it is
            var currentSemanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (currentSemanticModel == semanticModel)
            {
                return symbols;
            }

            // get symbols from current document again
            return SymbolFinder.FindSymbolAtPosition(currentSemanticModel, position, document.Project.Solution.Workspace, cancellationToken: cancellationToken);
        }

        private async Task<IEnumerable<DocumentHighlights>> GetTagsForReferencedSymbolAsync(
            ISymbol symbol,
            IImmutableSet<Document> documentsToSearch,
            Solution solution,
            CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(symbol);
            if (ShouldConsiderSymbol(symbol))
            {
                var references = await SymbolFinder.FindReferencesAsync(
                    symbol, solution, progress: null, documents: documentsToSearch, cancellationToken: cancellationToken).ConfigureAwait(false);

                return await FilterAndCreateSpansAsync(references, solution, documentsToSearch, symbol, cancellationToken).ConfigureAwait(false);
            }

            return SpecializedCollections.EmptyEnumerable<DocumentHighlights>();
        }

        private bool ShouldConsiderSymbol(ISymbol symbol)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.Method:
                    switch (((IMethodSymbol)symbol).MethodKind)
                    {
                        case MethodKind.AnonymousFunction:
                        case MethodKind.PropertyGet:
                        case MethodKind.PropertySet:
                        case MethodKind.EventAdd:
                        case MethodKind.EventRaise:
                        case MethodKind.EventRemove:
                            return false;

                        default:
                            return true;
                    }

                default:
                    return true;
            }
        }

        private async Task<IEnumerable<DocumentHighlights>> FilterAndCreateSpansAsync(
            IEnumerable<ReferencedSymbol> references, Solution solution, IImmutableSet<Document> documentsToSearch, ISymbol symbol, CancellationToken cancellationToken)
        {
            references = references.FilterUnreferencedSyntheticDefinitions();
            references = references.FilterNonMatchingMethodNames(solution, symbol);
            references = references.FilterToAliasMatches(symbol as IAliasSymbol);

            if (symbol.IsConstructor())
            {
                references = references.Where(r => r.Definition.OriginalDefinition.Equals(symbol.OriginalDefinition));
            }

            var additionalReferences = new List<Location>();

            foreach (var document in documentsToSearch)
            {
                additionalReferences.AddRange(await GetAdditionalReferencesAsync(document, symbol, cancellationToken).ConfigureAwait(false));
            }

            return await CreateSpansAsync(solution, symbol, references, additionalReferences, documentsToSearch, cancellationToken).ConfigureAwait(false);
        }

        private Task<IEnumerable<Location>> GetAdditionalReferencesAsync(
            Document document, ISymbol symbol, CancellationToken cancellationToken)
        {
            return document.Project.LanguageServices.GetService<IReferenceHighlightingAdditionalReferenceProvider>()
                .GetAdditionalReferencesAsync(document, symbol, cancellationToken);
        }

        private async Task<IEnumerable<DocumentHighlights>> CreateSpansAsync(
            Solution solution,
            ISymbol symbol,
            IEnumerable<ReferencedSymbol> references,
            IEnumerable<Location> additionalReferences,
            IImmutableSet<Document> documentToSearch,
            CancellationToken cancellationToken)
        {
            var spanSet = new HashSet<ValueTuple<Document, TextSpan>>();
            var tagMap = new MultiDictionary<Document, HighlightSpan>();
            bool addAllDefinitions = true;

            // Add definitions
            if (symbol.Kind == SymbolKind.Alias &&
                symbol.Locations.Length > 0)
            {
                // For alias symbol we want to get the tag only for the alias definition, not the target symbol's definition.
                await AddLocationSpan(symbol.Locations.First(), solution, spanSet, tagMap, true, cancellationToken).ConfigureAwait(false);
                addAllDefinitions = false;
            }

            // Add references and definitions
            foreach (var reference in references)
            {
                if (addAllDefinitions && ShouldIncludeDefinition(reference.Definition))
                {
                    foreach (var location in reference.Definition.Locations)
                    {
                        if (location.IsInSource && documentToSearch.Contains(solution.GetDocument(location.SourceTree)))
                        {
                            await AddLocationSpan(location, solution, spanSet, tagMap, true, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                foreach (var referenceLocation in reference.Locations)
                {
                    await AddLocationSpan(referenceLocation.Location, solution, spanSet, tagMap, false, cancellationToken).ConfigureAwait(false);
                }
            }

            // Add additional references
            foreach (var location in additionalReferences)
            {
                await AddLocationSpan(location, solution, spanSet, tagMap, false, cancellationToken).ConfigureAwait(false);
            }

            var list = new List<DocumentHighlights>(tagMap.Count);
            foreach (var kvp in tagMap)
            {
                var spans = new List<HighlightSpan>(kvp.Value.Count);
                foreach (var span in kvp.Value)
                {
                    spans.Add(span);
                }

                list.Add(new DocumentHighlights(kvp.Key, spans));
            }

            return list;
        }

        private static bool ShouldIncludeDefinition(ISymbol symbol)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.Namespace:
                    return false;

                case SymbolKind.NamedType:
                    return !((INamedTypeSymbol)symbol).IsScriptClass;

                case SymbolKind.Parameter:

                    // If it's an indexer parameter, we will have also cascaded to the accessor
                    // one that actually receives the references
                    var containingProperty = symbol.ContainingSymbol as IPropertySymbol;
                    if (containingProperty != null && containingProperty.IsIndexer)
                    {
                        return false;
                    }

                    break;
            }

            return true;
        }

        private async Task AddLocationSpan(Location location, Solution solution, HashSet<ValueTuple<Document, TextSpan>> spanSet, MultiDictionary<Document, HighlightSpan> tagList, bool isDefinition, CancellationToken cancellationToken)
        {
            var span = await GetLocationSpanAsync(solution, location, cancellationToken).ConfigureAwait(false);
            if (span != null && !spanSet.Contains(span.Value))
            {
                spanSet.Add(span.Value);
                tagList.Add(span.Value.Item1, new HighlightSpan(span.Value.Item2, isDefinition));
            }
        }

        private async Task<ValueTuple<Document, TextSpan>?> GetLocationSpanAsync(Solution solution, Location location, CancellationToken cancellationToken)
        {
            var tree = location.SourceTree;

            var document = solution.GetDocument(tree);
            var syntaxFacts = document.Project.LanguageServices.GetService<ISyntaxFactsService>();

            // Specify findInsideTrivia: true to ensure that we search within XML doc comments.
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var token = root.FindToken(location.SourceSpan.Start, findInsideTrivia: true);

            return syntaxFacts.IsGenericName(token.Parent) || syntaxFacts.IsIndexerMemberCRef(token.Parent)
                ? ValueTuple.Create(document, token.Span)
                : ValueTuple.Create(document, location.SourceSpan);
        }
    }
}
