export type AgentMessageRole = "user" | "agent";

export type AgentSource = {
  label: string;
};

export type AgentMessage = {
  id: string;
  role: AgentMessageRole;
  sources?: readonly AgentSource[];
  text: string;
};

export type AgentProposal = {
  amount: number;
  id: string;
  summary: string;
  title: string;
};

export type AgentStatus = "idle" | "thinking";
