@{
    SchemaVersion = 2
    Id = 'grandtheftautov'
    DisplayName = 'Grand Theft Auto V'
    Module = 'Grid.Health.GtaV.psm1'
    InvocationFunction = 'Invoke-GridGtaHealthAdapter'
    CapabilityManifest = '..\capabilities.v1.json'
    InvocationCapabilityId = 'grid.game.grandtheftautov.investigation.collect'
    BaselineCapabilityId = 'grid.game.grandtheftautov.baseline.collect'
    ConnectedContextCapabilityId = 'grid.game.grandtheftautov.context.probe'
    DefaultPipelineCapabilityIds = @('grid.game.grandtheftautov.investigation.collect')
    PromptMatchers = @('gta v', 'grand theft auto v', 'openiv', 'menyoo', 'scripthookv', 'forever together')
    ConnectedContextScript = 'collectors/Test-GridGtaConnectedContext.ps1'
    ConnectedContextFunction = 'Test-GridGtaConnectedContext'
}
