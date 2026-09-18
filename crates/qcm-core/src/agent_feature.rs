//! Agent feature gate. Matches Avalonia `AgentFeature.Enabled = false`.
//! Flip to true when TASK-048 UI returns; corpus/eval stays under `agent/`.

/// When false, no agent surface is exposed (`capabilities.agent` stays false).
pub const ENABLED: bool = false;

#[cfg(test)]
mod tests {
    #[test]
    fn agent_stays_off_like_avalonia() {
        assert!(!super::ENABLED);
    }
}
