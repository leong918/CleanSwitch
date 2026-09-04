# Pre-physical deployment test gates

`scripts/Test-PrePhysicalDeployment.ps1` is the required readiness profile. A readiness claim is forbidden unless it completes with zero failures and zero skipped mandatory tests.

Mandatory environment-backed suites:

- `LiveTestBuildFact`: production destructive gates with fake process boundaries.
- `CombinedRetirementIntegrationFact`: disposable VHD plus isolated BCD orchestration.
- `VhdIntegrationFact`: disposable non-system VHD partition operations.
- `BcdIntegrationFact`: isolated `bcdedit /store` operations.
- `WinReWimIntegrationFact`: disposable WIM servicing fixture.
- `WinReDeploymentVmIntegrationFact`: three disposable VM prepare/deploy/review/smoke/rollback cycles.

Optional skips are limited to tests unrelated to physical retirement readiness. Any skip from the six categories above makes the readiness profile fail.
