using Forge.Core;

namespace Forge.Tests;

public class ExtensionPermissionsTests
{
	private static ExtensionPermissions Create(out string path) {
		path = Path.Combine(Path.GetTempPath(), $"forge-permissions-{Guid.NewGuid():N}.json");
		return new ExtensionPermissions(path);
	}

	[Fact]
	public void RulesPersistForSameVersion() {
		ExtensionPermissions permissions = Create(out string path);

		try {
			permissions.Set("anthropic.claude-code", "2.1.0", "process", "claude.exe", true);

			Assert.Single(new ExtensionPermissions(path).Rules("anthropic.claude-code", "2.1.0"));
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void VersionChangeResetsRules() {
		ExtensionPermissions permissions = Create(out string path);

		try {
			permissions.Set("anthropic.claude-code", "2.1.0", "network", "api.anthropic.com", true);

			Assert.True(permissions.Reconcile("anthropic.claude-code", "2.1.1"));
			Assert.Empty(permissions.Rules("anthropic.claude-code", "2.1.0"));
			Assert.Empty(permissions.Rules("anthropic.claude-code", "2.1.1"));
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void SettingAgainReplacesDecision() {
		ExtensionPermissions permissions = Create(out string path);

		try {
			permissions.Set("a.b", "1.0.0", "write", @"C:\Users\x\.cache", true);
			permissions.Set("a.b", "1.0.0", "write", @"c:\users\x\.cache", false);

			Assert.False(Assert.Single(permissions.Rules("a.b", "1.0.0")).Allowed);
		}
		finally {
			File.Delete(path);
		}
	}
}
