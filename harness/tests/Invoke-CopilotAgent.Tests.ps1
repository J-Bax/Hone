BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-CopilotAgent.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $null = $harnessRoot, $scriptPath, $configPath
}

Describe 'Invoke-CopilotAgent mock responses' {
    It 'parses JSON from a mock response and saves the raw response' {
        $mockPath = Join-Path -Path $TestDrive -ChildPath 'mock-response.json'
        $responsePath = Join-Path -Path $TestDrive -ChildPath 'saved-response.json'

        '{"scope":"narrow","reasoning":"Looks isolated"}' | Set-Content -Path $mockPath -Encoding ascii

        $result = & $scriptPath `
            -AgentName 'hone-classifier' `
            -Prompt 'Classify this' `
            -ConfigPath $configPath `
            -MockResponsePath $mockPath `
            -ResponsePath $responsePath

        $result.Success | Should -BeTrue
        $result.ExitCode | Should -Be 0
        $result.TimedOut | Should -BeFalse
        $result.ParsedJson.scope | Should -Be 'narrow'
        $result.ParsedJson.reasoning | Should -Be 'Looks isolated'
        Test-Path $responsePath | Should -BeTrue
    }

    It 'returns null ParsedJson for non-JSON mock responses without failing' {
        $mockPath = Join-Path -Path $TestDrive -ChildPath 'mock-response.txt'
        'not json at all' | Set-Content -Path $mockPath -Encoding ascii

        $result = & $scriptPath `
            -AgentName 'hone-fixer' `
            -Prompt 'Fix this' `
            -ConfigPath $configPath `
            -MockResponsePath $mockPath

        $result.Success | Should -BeTrue
        $result.ParsedJson | Should -BeNullOrEmpty
        $result.ResponseText | Should -Be 'not json at all'
    }
}
