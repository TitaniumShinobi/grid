@{
    SchemaVersion = 2
    Id = 'skyrimspecialedition'
    DisplayName = 'Skyrim Special Edition'
    Module = 'Grid.Health.Skyrim.psm1'
    InvocationFunction = 'Invoke-GridSkyrimHealthAdapter'
    CapabilityManifest = '..\capabilities.v1.json'
    InvocationCapabilityId = 'grid.game.skyrimspecialedition.investigation.collect'
    BaselineCapabilityId = 'grid.game.skyrimspecialedition.baseline.collect'
    RepairInspectionCapabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair.inspect'
    RepairProposalCapabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair-proposal.materialize'
    RepairExecutionCapabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair.execute'
    ArtifactAcquisitionCapabilityId = 'grid.game.skyrimspecialedition.artifact.acquire'
    RuntimeCertificationEligibilityCapabilityId = 'grid.game.skyrimspecialedition.runtime-certification.eligibility.resolve'
    RuntimeCertificationCapabilityId = 'grid.game.skyrimspecialedition.runtime-certification.evaluate'
    RuntimeCertificationSessionCapabilityId = 'grid.game.skyrimspecialedition.runtime-certification.session.execute'
    RootCauseCapabilityId = 'grid.game.skyrimspecialedition.root-cause.resolve'
    ConnectedContextCapabilityId = 'grid.game.skyrimspecialedition.context.probe'
    DefaultPipelineCapabilityIds = @('grid.game.skyrimspecialedition.investigation.collect')
    # Generic Skyrim/MO2/xEdit vocabulary only. No location, mod, or symptom identities.
    PromptMatchers = @('skyrim', 'mo2', 'mod organizer', 'sseedit', 'xedit', 'esp', 'esm', 'esl', 'nif', 'dds', 'load order', 'plugin')
    ConnectedContextScript = 'collectors/Test-GridSkyrimConnectedContext.ps1'
    ConnectedContextFunction = 'Test-GridSkyrimConnectedContext'
}
