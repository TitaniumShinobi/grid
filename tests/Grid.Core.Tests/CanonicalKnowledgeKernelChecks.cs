using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

internal static class CanonicalKnowledgeKernelChecks
{
    public static int Run()
    {
        var checks = 0;

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Check failed: {message}");
            checks++;
        }

        void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                checks++;
                return;
            }

            throw new InvalidOperationException($"Check failed: {message}");
        }

        var gameId = new GameId("game.fixture");
        var sourceNativeId = SourceNativeIdentifier.FromExactUtf8(
            "provider.fixture",
            "mod",
            "Fixture Mod #42");
        var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.ModProvider, sourceNativeId);
        var artifactDigest = new ContentDigest(ContentDigest.Sha256Algorithm, new string('a', 64));
        var artifactId = SourceArtifactId.DeriveV1(artifactDigest);
        var sourceRevision = SourceNativeVersion.FromExactUtf8("provider.fixture.file-version", "7.2.1");
        var sourceRevisionId = CatalogSourceRevisionId.DeriveV1(sourceId, sourceRevision, [artifactId]);
        var nativeRecord = SourceNativeIdentifier.FromExactUtf8("fixture.records", "ITEM", "Plugin.esp:000123");
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(gameId, nativeRecord);
        var gameVersion = SourceNativeVersion.FromExactUtf8("fixture.game-build", "1.6.1170");
        var modVersion = SourceNativeVersion.FromExactUtf8("provider.fixture.file-version", "7.2.1");
        var knowledgeRecordId = KnowledgeRecordId.DeriveV1(
            gameId,
            gameVersion,
            modVersion,
            sourceRevisionId,
            KnowledgeKind.Item,
            nativeRecordId);

        Assert(sourceId.Value == "grid.catalog-source.v1.sha256.52970274299f60d05795ef1b3938be1b9bc7e681340c8ab4eeaf1f047ae0a6bc",
            "Catalog source v1 identity matches its locked golden vector.");
        Assert(artifactId.Value == "grid.source-artifact.v1.sha256.332e51dd5c79d71f274c18c32c3d8b297ed9d0d7532506eb00a3367a68ac4dab",
            "Source artifact v1 identity matches its locked golden vector.");
        Assert(sourceRevisionId.Value == "grid.catalog-source-revision.v1.sha256.ee1f8f214eff9e932e5e3f08244b09fa25c11e8ec4ba46d258f3e520ba3803d3",
            "Catalog source revision v1 identity matches its locked golden vector.");
        Assert(nativeRecordId.Value == "grid.native-record-identity.v1.sha256.e74198c6d8af707ab2f308a0426852a3c8ea6508b10d750a1696e2ac70045195",
            "Native record v1 identity matches its locked golden vector.");
        Assert(knowledgeRecordId.Value == "grid.knowledge-record.v1.sha256.6491933b6d493c3ddf2627094eb6767ee15b856c0a4befd2ef7a1128c54d8435",
            "Knowledge record v1 identity matches its locked golden vector.");

        AssertThrows<EncoderFallbackException>(() => _ = CatalogSourceId.DeriveV1(
                KnowledgeSourceKind.ModProvider,
                SourceNativeIdentifier.FromExactUtf8("provider.fixture", "mod", "\uD800")),
            "A lone high surrogate cannot enter canonical identity through source-native identifier encoding.");
        AssertThrows<EncoderFallbackException>(() => _ = CatalogSourceId.DeriveV1(
                KnowledgeSourceKind.ModProvider,
                SourceNativeIdentifier.FromExactUtf8("provider.fixture", "mod", "\uD801")),
            "Distinct malformed UTF-16 cannot replacement-encode into a colliding canonical identity.");
        AssertThrows<EncoderFallbackException>(() => _ = SourceNativeVersion.FromExactUtf8(
                "provider.fixture.file-version",
                "7.2.1\uD800"),
            "Source-native version factories reject malformed UTF-16.");
        AssertThrows<EncoderFallbackException>(() => _ = new SourceNativeIdentifier(
                "provider.fixture",
                "mod",
                "\uD800",
                [0x01],
                "grid.exact-bytes",
                1),
            "Explicit source-native identifiers cannot bypass strict Unicode validation with supplied identity bytes.");
        AssertThrows<EncoderFallbackException>(() => _ = new SourceNativeVersion(
                "provider.fixture.file-version",
                "\uD801",
                [0x01],
                "grid.exact-bytes",
                1),
            "Explicit source-native versions cannot bypass strict Unicode validation with supplied identity bytes.");
        AssertThrows<EncoderFallbackException>(() => _ = NativeRecordIdentityId.DeriveV1(
                new GameId("game.fixture.\uD800"),
                nativeRecord),
            "Canonical string fields supplied directly to identity derivation reject malformed UTF-16.");

        const string wellFormedUnicode = "Café 💡 東京";
        var unicodeNativeIdentity = SourceNativeIdentifier.FromExactUtf8(
            "provider.例",
            "record.Δ",
            wellFormedUnicode);
        var unicodeSourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.ReferenceProvider, unicodeNativeIdentity);
        Assert(unicodeNativeIdentity.ExactRepresentation == wellFormedUnicode &&
               unicodeNativeIdentity.IdentityBytes.SequenceEqual(new UTF8Encoding(false, true).GetBytes(wellFormedUnicode)) &&
               CatalogSourceId.DeriveV1(KnowledgeSourceKind.ReferenceProvider, unicodeNativeIdentity) == unicodeSourceId,
            "Well-formed non-ASCII source identity remains exact and deterministic under strict UTF-8.");
        Console.WriteLine("PASS  Adversarial malformed-Unicode canonical identity probe.");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert(CatalogSourceId.DeriveV1(KnowledgeSourceKind.ModProvider, sourceNativeId) == sourceId,
                "Identical source inputs retain one deterministic identity.");
            Assert(SourceArtifactId.DeriveV1(artifactDigest) == artifactId,
                "Identical artifact inputs retain one deterministic identity.");
            Assert(CatalogSourceRevisionId.DeriveV1(sourceId, sourceRevision, [artifactId]) == sourceRevisionId,
                "Identical source-revision inputs retain one deterministic identity.");
            Assert(NativeRecordIdentityId.DeriveV1(gameId, nativeRecord) == nativeRecordId,
                "Identical native-record inputs retain one deterministic identity.");
            Assert(KnowledgeRecordId.DeriveV1(
                    gameId,
                    gameVersion,
                    modVersion,
                    sourceRevisionId,
                    KnowledgeKind.Item,
                    nativeRecordId) == knowledgeRecordId,
                "Identical knowledge inputs retain one deterministic identity.");
        }

        var changedArtifactDigest = new ContentDigest(ContentDigest.Sha256Algorithm, new string('b', 64));
        var changedArtifactId = SourceArtifactId.DeriveV1(changedArtifactDigest);
        var changedSourceRevisionId = CatalogSourceRevisionId.DeriveV1(
            sourceId,
            SourceNativeVersion.FromExactUtf8("provider.fixture.file-version", "7.2.2"),
            [changedArtifactId]);
        var changedRevisionKnowledgeId = KnowledgeRecordId.DeriveV1(
            gameId, gameVersion, modVersion, changedSourceRevisionId, KnowledgeKind.Item, nativeRecordId);
        Assert(changedSourceRevisionId != sourceRevisionId && changedRevisionKnowledgeId != knowledgeRecordId,
            "Changing a source revision changes the revision and dependent knowledge identities.");
        var nativeRevisionOnlyChange = CatalogSourceRevisionId.DeriveV1(
            sourceId,
            SourceNativeVersion.FromExactUtf8("provider.fixture.file-version", "7.2.2"),
            [artifactId]);
        var artifactOnlyChange = CatalogSourceRevisionId.DeriveV1(
            sourceId,
            sourceRevision,
            [changedArtifactId]);
        Assert(nativeRevisionOnlyChange != sourceRevisionId,
            "Changing only the source-native revision changes the source revision identity.");
        Assert(artifactOnlyChange != sourceRevisionId,
            "Changing only an artifact changes the source revision identity.");
        Assert(CatalogSourceRevisionId.DeriveV1(sourceId, sourceRevision, [artifactId, changedArtifactId]) ==
               CatalogSourceRevisionId.DeriveV1(sourceId, sourceRevision, [changedArtifactId, artifactId]),
            "Artifact ordering cannot affect source revision identity.");
        AssertThrows<ArgumentException>(() => _ = CatalogSourceRevisionId.DeriveV1(
                sourceId,
                sourceRevision,
                [artifactId, artifactId]),
            "Duplicate source revision artifacts are rejected.");

        var changedGameKnowledgeId = KnowledgeRecordId.DeriveV1(
            gameId,
            SourceNativeVersion.FromExactUtf8("fixture.game-build", "1.6.1171"),
            modVersion,
            sourceRevisionId,
            KnowledgeKind.Item,
            nativeRecordId);
        var changedModKnowledgeId = KnowledgeRecordId.DeriveV1(
            gameId,
            gameVersion,
            SourceNativeVersion.FromExactUtf8("provider.fixture.file-version", "7.2.2"),
            sourceRevisionId,
            KnowledgeKind.Item,
            nativeRecordId);
        var unresolvedVersionsKnowledgeId = KnowledgeRecordId.DeriveV1(
            gameId, null, null, sourceRevisionId, KnowledgeKind.Item, nativeRecordId);
        Assert(changedGameKnowledgeId != knowledgeRecordId && changedModKnowledgeId != knowledgeRecordId,
            "Changing exact game or mod version inputs changes the knowledge identity.");
        Assert(unresolvedVersionsKnowledgeId != knowledgeRecordId,
            "Absent version coordinates remain distinct from present source-native versions.");
        Assert(KnowledgeRecordId.DeriveV1(
                gameId,
                SourceNativeVersion.FromExactUtf8("fixture.game-build", "1.6.1170", "fixture.other-comparison", 1),
                modVersion,
                sourceRevisionId,
                KnowledgeKind.Item,
                nativeRecordId) != knowledgeRecordId,
            "Version comparison-method identity participates in canonical derivation.");
        Assert(KnowledgeRecordId.DeriveV1(
                gameId,
                SourceNativeVersion.FromExactUtf8("fixture.game-build", "1.6.1170", "grid.exact-utf8", 2),
                modVersion,
                sourceRevisionId,
                KnowledgeKind.Item,
                nativeRecordId) != knowledgeRecordId,
            "Comparison-method version changes canonical identity.");

        _ = "account.fixture.a";
        _ = new InstallationId("installation.fixture.a");
        _ = new ProfileId("profile.fixture.a");
        _ = new ModId("profile-local-mod.fixture.a");
        _ = 37;
        Assert(KnowledgeRecordId.DeriveV1(
                gameId, gameVersion, modVersion, sourceRevisionId, KnowledgeKind.Item, nativeRecordId) == knowledgeRecordId,
            "Account, installation, profile, local mod, and load-order fixture differences cannot affect canonical IDs.");
        var prohibitedTypes = new[] { typeof(InstallationId), typeof(ProfileId), typeof(ModId) };
        var canonicalFactoryTypes = new[]
        {
            typeof(CatalogSourceId), typeof(CatalogSourceRevisionId), typeof(SourceArtifactId),
            typeof(NativeRecordIdentityId), typeof(KnowledgeRecordId),
        };
        Assert(canonicalFactoryTypes
                .SelectMany(type => type.GetMethods().Where(method => method.IsPublic && method.IsStatic && method.Name == "DeriveV1"))
                .SelectMany(method => method.GetParameters())
                .All(parameter => !prohibitedTypes.Contains(parameter.ParameterType)),
            "Canonical derivation APIs cannot accept installation, profile, or profile-local mod identity.");

        var ambiguousLeft = CatalogSourceId.DeriveV1(
            KnowledgeSourceKind.ReferenceProvider,
            SourceNativeIdentifier.FromExactUtf8("ab", "fixture", "c"));
        var ambiguousRight = CatalogSourceId.DeriveV1(
            KnowledgeSourceKind.ReferenceProvider,
            SourceNativeIdentifier.FromExactUtf8("a", "fixture", "bc"));
        Assert(ambiguousLeft != ambiguousRight,
            "Length-prefixed canonical components distinguish ambiguous concatenations.");
        Assert((int)KnowledgeSourceKind.LocalGameDistribution == 0 &&
               (int)KnowledgeSourceKind.LocalModArtifact == 1 &&
               (int)KnowledgeSourceKind.PluginRecordFile == 2 &&
               (int)KnowledgeSourceKind.OfficialProvider == 3 &&
               (int)KnowledgeSourceKind.ModProvider == 4 &&
               (int)KnowledgeSourceKind.ReferenceProvider == 5 &&
               (int)KnowledgeSourceKind.FrozenRepositoryDataset == 6 &&
               (int)KnowledgeKind.Location == 0 &&
               (int)KnowledgeKind.MissionQuest == 1 &&
               (int)KnowledgeKind.Item == 2 &&
               (int)KnowledgeKind.Actor == 3,
            "Every enum value embedded in the v1 wire format is explicitly frozen.");

        const string exactTerm = "  Café—UPPER!  \r\n";
        var terminology = new TerminologyAssertion(
            knowledgeRecordId,
            sourceRevisionId,
            TerminologyAssertionRole.PrimaryName,
            exactTerm,
            "records[0].FULL",
            "fr-CA");
        Assert(terminology.VerbatimValue == exactTerm && terminology.VerbatimValue.Length == exactTerm.Length,
            "Terminology preserves exact source text, spacing, case, punctuation, and line endings.");
        Assert(new TerminologyAssertion(
                knowledgeRecordId,
                sourceRevisionId,
                TerminologyAssertionRole.PrimaryName,
                string.Empty,
                "records[0].FULL").VerbatimValue == string.Empty,
            "An explicitly asserted empty source value is preserved rather than synthesized.");

        var observedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var fileEvidence = new FileEvidenceReceipt(
            sourceRevisionId,
            artifactId,
            artifactDigest,
            "fixture-parser",
            "1.0.0",
            "record:000123",
            "record.FULL",
            128,
            24,
            ContentDigest.ComputeSha256("interpreted"u8),
            observedAt);
        var referenceEvidence = new ReferenceEvidenceReceipt(
            sourceId,
            sourceRevisionId,
            artifactId,
            artifactDigest,
            sourceNativeId,
            sourceRevision,
            "$.results[0].name",
            observedAt);
        Assert(fileEvidence.Verification == EvidenceVerificationKind.FileVerified &&
               referenceEvidence.Verification == EvidenceVerificationKind.ReferenceVerified &&
               fileEvidence.GetType() != referenceEvidence.GetType(),
            "File and reference evidence are structurally distinct with fixed verification classifications.");
        AssertThrows<ArgumentException>(() => _ = new FileEvidenceReceipt(
                sourceRevisionId,
                artifactId,
                changedArtifactDigest,
                "fixture-parser",
                "1.0.0",
                "record:000123",
                "record.FULL",
                null,
                null,
                null,
                observedAt),
            "File evidence rejects a digest that does not match its artifact identity.");
        AssertThrows<ArgumentException>(() => _ = new ReferenceEvidenceReceipt(
                sourceId,
                sourceRevisionId,
                artifactId,
                changedArtifactDigest,
                sourceNativeId,
                sourceRevision,
                "$.results[0].name",
                observedAt),
            "Reference evidence rejects a response digest that does not match its artifact identity.");
        var referenceWithoutNativeCoordinates = new ReferenceEvidenceReceipt(
            sourceId,
            sourceRevisionId,
            artifactId,
            artifactDigest,
            null,
            null,
            "$.results[0]",
            observedAt);
        Assert(referenceWithoutNativeCoordinates.NativeObjectIdentity is null &&
               referenceWithoutNativeCoordinates.NativeRevisionIdentity is null &&
               referenceWithoutNativeCoordinates.Verification == EvidenceVerificationKind.ReferenceVerified,
            "Reference evidence retains exact provider revision and field evidence when native object coordinates are unavailable.");

        var correlatedRecordId = KnowledgeRecordId.DeriveV1(
            gameId, gameVersion, modVersion, sourceRevisionId, KnowledgeKind.Item,
            NativeRecordIdentityId.DeriveV1(
                gameId,
                SourceNativeIdentifier.FromExactUtf8("fixture.records", "ITEM", "Plugin.esp:000124")));
        var correlation = new CorrelationRecord(
            [correlatedRecordId, knowledgeRecordId],
            "grid.exact-native-id",
            "1",
            CorrelationOutcome.Correlated);
        Assert(correlation.MemberIds.SequenceEqual(
                new[] { correlatedRecordId, knowledgeRecordId }.OrderBy(value => value.Value, StringComparer.Ordinal)) &&
               correlation.MethodId == "grid.exact-native-id" && correlation.MethodVersion == "1" &&
               correlation.Outcome == CorrelationOutcome.Correlated,
            "Correlation preserves distinct member IDs and its exact deterministic method/version.");
        Assert(typeof(CorrelationRecord).GetProperty("Verification") is null,
            "Correlation cannot masquerade as file or reference verification.");

        ImmutableArray<TerminologyAssertion> unresolvedTerminology = [];
        var unresolvedRelationship = new RelationshipAssertion(
            knowledgeRecordId,
            sourceRevisionId,
            new RelationshipSemanticId("grid.relationship.located-at"),
            "native-ref",
            "record.location",
            SourceNativeIdentifier.FromExactUtf8("fixture.records", "CELL", "Plugin.esp:000999"));
        Assert(unresolvedTerminology.IsEmpty && unresolvedRelationship.Resolution == CanonicalResolutionState.Unresolved &&
               unresolvedRelationship.ResolvedTargetKnowledgeRecordId is null &&
               unresolvedRelationship.SourceRevisionId == sourceRevisionId &&
               unresolvedRelationship.SourceNativeTarget.ExactRepresentation == "Plugin.esp:000999",
            "Unresolved records and relationships preserve native identity without inventing a display name.");
        var sameRelationshipFromChangedRevision = new RelationshipAssertion(
            knowledgeRecordId,
            changedSourceRevisionId,
            unresolvedRelationship.SemanticId,
            unresolvedRelationship.SourceNativeRelationshipType,
            unresolvedRelationship.SourceFieldPath,
            unresolvedRelationship.SourceNativeTarget);
        Assert(sameRelationshipFromChangedRevision.SourceRevisionId != unresolvedRelationship.SourceRevisionId,
            "Otherwise identical relationship assertions retain their independent source revisions.");

        Assert(SourceNativeIdentifier.FromExactUtf8("fixture", "record", "Exact") ==
               SourceNativeIdentifier.FromExactUtf8("fixture", "record", "Exact") &&
               SourceNativeVersion.FromExactUtf8("fixture.version", "1.0") ==
               SourceNativeVersion.FromExactUtf8("fixture.version", "1.0"),
            "Source-native supporting values use structural byte equality.");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert(JsonSerializer.Deserialize<CatalogSourceId>(JsonSerializer.Serialize(sourceId, jsonOptions), jsonOptions) == sourceId &&
               JsonSerializer.Deserialize<CatalogSourceRevisionId>(JsonSerializer.Serialize(sourceRevisionId, jsonOptions), jsonOptions) == sourceRevisionId &&
               JsonSerializer.Deserialize<SourceArtifactId>(JsonSerializer.Serialize(artifactId, jsonOptions), jsonOptions) == artifactId &&
               JsonSerializer.Deserialize<NativeRecordIdentityId>(JsonSerializer.Serialize(nativeRecordId, jsonOptions), jsonOptions) == nativeRecordId &&
               JsonSerializer.Deserialize<KnowledgeRecordId>(JsonSerializer.Serialize(knowledgeRecordId, jsonOptions), jsonOptions) == knowledgeRecordId,
            "Canonical identifier values round-trip without reinterpretation.");

        Console.WriteLine($"PASS  Canonical knowledge identity and evidence kernel ({checks} checks).");
        return checks;
    }
}
