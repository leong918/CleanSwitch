# Pre-physical deployment test gates

`scripts/Test-PrePhysicalDeployment.ps1` is the required readiness profile. A readiness claim is forbidden unless it completes with zero failures and zero skipped mandatory tests.

Mandatory environment-backed suites:

- `LiveTestBuildFact`: production destructive gates with fake process boundaries.
- `CombinedRetirementIntegrationFact`: disposable VHD plus isolated BCD orchestration.
- `VhdIntegrationFact`: disposable non-system VHD partition operations.
- `BcdIntegrationFact`: isolated `bcdedit /store` operations.
- `WinReWimIntegrationFact`: disposable WIM servicing fixture.
- `WinReDeploymentVmIntegrationFact`: three independent disposable VM end-to-end retirement cycles. Each cycle restores the same pristine host-hypervisor checkpoint, deploys and reviews WinRE, explicitly runs transaction-bound `--commit-winre-deployment`, initiates the product-supported `RETIRE SYSTEM` flow from Boot1, proves automatic `--recovery-launch` retirement execution, proves Boot1 partition/BCD removal and Boot2 `COMPLETE`, collects a hashed artifact manifest, stops the VM, and only then restores the pristine checkpoint for test reset. Hypervisor restore is not a CleanSwitch rollback.

Optional skips are limited to tests unrelated to physical retirement readiness. Any skip from the six categories above makes the readiness profile fail.
