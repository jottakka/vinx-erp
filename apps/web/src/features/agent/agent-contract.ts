export type AgentMessageRole = "user" | "agent";

export type AgentSource = {
  label: string;
};

export type AgentMessage = {
  id: string;
  proposalPath?: string;
  role: AgentMessageRole;
  sources?: readonly AgentSource[];
  text: string;
};

export type AgentStatus = "idle" | "thinking";
