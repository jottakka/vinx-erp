import { zodResolver } from "@hookform/resolvers/zod";
import {
  Alert,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormHelperText,
  InputLabel,
  List,
  ListItem,
  MenuItem,
  Select,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { MutableRefObject, ReactElement } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { useFeedback } from "../../shared/ui/feedback-provider";
import { PageHeader } from "../../shared/ui/page-header";
import { hasCompanyCapability } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  createPermissionsClient,
  type PermissionsAssignmentCommand,
  type PermissionsCopyCommand,
  type PermissionsFailure,
  type PermissionsGateway,
  type PermissionsInvitationStatus,
  type PermissionsInviteCommand,
  type PermissionsMember,
  type PermissionsRole,
} from "./permissions-client";

const permissionsCapability = "foundation.roles.assign";
const membersQueryKey = "permissions-members";
const rolesQueryKey = "permissions-roles";
const sessionFailureMessage =
  "Sua sessão não permite mais esta ação. Entre novamente.";
const attemptWarningMessage =
  "A tentativa anterior terminou sem resposta confirmada. Tente novamente com os mesmos dados.";
const lastAdminGuardMessage =
  "A empresa precisa de ao menos um administrador ativo.";
const memberConflictMessage = "Este e-mail já possui cadastro.";
const rateLimitedMessage =
  "Muitas tentativas. Aguarde antes de tentar novamente.";
const conflictMessage =
  "Não foi possível concluir: já existe um registro conflitante.";

const inviteFormSchema = z.object({
  displayName: z.string().trim().min(1, "Informe um nome válido.").max(200),
  email: z
    .string()
    .trim()
    .min(1, "Informe um e-mail válido.")
    .max(320)
    .email("Informe um e-mail válido."),
});

type InviteForm = z.infer<typeof inviteFormSchema>;
type PermissionsPageProps = {
  gateway?: PermissionsGateway;
  createRequestId?: () => string;
};

type MutationKind = "assign" | "unassign" | "copy" | "invite";
type PermissionsOperation = (
  | { kind: "assign"; command: PermissionsAssignmentCommand }
  | { kind: "unassign"; command: PermissionsAssignmentCommand }
  | { kind: "copy"; command: PermissionsCopyCommand }
  | { kind: "invite"; command: PermissionsInviteCommand }
) & { companyId: string; generation: number };
type PageFeedback = { message: string; severity: "error" | "warning" } | null;

const invitationChipByStatus: Record<
  PermissionsInvitationStatus,
  { label: string; warning: boolean }
> = {
  pending_delivery: { label: "Convite pendente", warning: false },
  pending_provider: { label: "Convite pendente", warning: false },
  provider_in_flight: { label: "Convite pendente", warning: false },
  pending_acceptance: { label: "Convite enviado", warning: false },
  provider_failed: { label: "Falha na entrega", warning: true },
  provider_ambiguous: { label: "Entrega incerta", warning: true },
};

export function PermissionsPage({
  gateway = createPermissionsClient(),
  createRequestId = () => crypto.randomUUID(),
}: PermissionsPageProps): ReactElement {
  const { bootstrap, refreshBootstrap, status } = useSession();
  const { notify } = useFeedback();
  const queryClient = useQueryClient();
  const [selectedCompanyId, setSelectedCompanyId] = useState("");
  const [manageDialogMemberId, setManageDialogMemberId] = useState<string | null>(
    null,
  );
  const [manageDialogRoleId, setManageDialogRoleId] = useState("");
  const [manageDialogSourceId, setManageDialogSourceId] = useState("");
  const [memberSearch, setMemberSearch] = useState("");
  const [inviteDialogOpen, setInviteDialogOpen] = useState(false);
  const [attempt, setAttempt] = useState<PermissionsOperation | null>(null);
  const [feedback, setFeedback] = useState<PageFeedback>(null);
  const operationGeneration = useRef(0);
  const inFlight = useRef(false);
  const attemptRef = useRef<PermissionsOperation | null>(null);
  const processedReadFailure = useRef<string | null>(null);
  const selectedCompanyIdRef = useRef("");
  const inviteForm = useForm<InviteForm>({
    defaultValues: { displayName: "", email: "" },
    resolver: zodResolver(inviteFormSchema),
  });

  const eligibleCompanies = useMemo(
    () =>
      (bootstrap?.companies ?? []).filter((company) =>
        hasCompanyCapability(company, permissionsCapability),
      ),
    [bootstrap],
  );
  const selectedCompany = eligibleCompanies.find(
    (company) => company.companyId === selectedCompanyId,
  );
  selectedCompanyIdRef.current = selectedCompany?.companyId ?? "";

  const members = useQuery({
    enabled: Boolean(selectedCompany),
    queryFn: ({ signal }) =>
      selectedCompany
        ? gateway.listMembers(selectedCompany.companyId, signal)
        : Promise.resolve({
            error: { kind: "invalid_request" as const },
            ok: false as const,
          }),
    queryKey: [membersQueryKey, selectedCompany?.companyId ?? "none"],
    retry: false,
  });
  const roles = useQuery({
    enabled: Boolean(selectedCompany),
    queryFn: ({ signal }) =>
      selectedCompany
        ? gateway.listRoles(selectedCompany.companyId, signal)
        : Promise.resolve({
            error: { kind: "invalid_request" as const },
            ok: false as const,
          }),
    queryKey: [rolesQueryKey, selectedCompany?.companyId ?? "none"],
    retry: false,
  });

  // O cancelamento do diálogo descarta a tentativa ambígua: novas ações usam
  // novo requestId e o servidor responde com conflito idempotente se a
  // tentativa anterior tiver sido confirmada.
  const abandonAttempt = useCallback(() => {
    if (attemptRef.current) {
      operationGeneration.current += 1;
      setAttempt(null);
      attemptRef.current = null;
    }
  }, []);

  const closeManageDialog = useCallback(() => {
    abandonAttempt();
    setFeedback(null);
    setManageDialogMemberId(null);
    setManageDialogRoleId("");
    setManageDialogSourceId("");
  }, [abandonAttempt]);

  const closeInviteDialog = useCallback(() => {
    abandonAttempt();
    setFeedback(null);
    setInviteDialogOpen(false);
  }, [abandonAttempt]);

  const neutralize = useCallback((message: PageFeedback) => {
    operationGeneration.current += 1;
    processedReadFailure.current = null;
    setSelectedCompanyId("");
    setMemberSearch("");
    setManageDialogMemberId(null);
    setManageDialogRoleId("");
    setManageDialogSourceId("");
    setInviteDialogOpen(false);
    setAttempt(null);
    attemptRef.current = null;
    setFeedback(message);
  }, []);

  const selectCompany = (companyId: string) => {
    if (companyId === selectedCompanyId) return;
    operationGeneration.current += 1;
    processedReadFailure.current = null;
    setSelectedCompanyId(companyId);
    setMemberSearch("");
    setManageDialogMemberId(null);
    setManageDialogRoleId("");
    setManageDialogSourceId("");
    setInviteDialogOpen(false);
    setAttempt(null);
    attemptRef.current = null;
    setFeedback(null);
  };

  const mutation = useMutation({
    mutationFn: (operation: PermissionsOperation) => {
      switch (operation.kind) {
        case "assign":
          return gateway.assignRole(operation.command);
        case "unassign":
          return gateway.unassignRole(operation.command);
        case "copy":
          return gateway.copyPermissions(operation.command);
        case "invite":
          return gateway.inviteMember(operation.command);
      }
    },
    onSettled: () => {
      inFlight.current = false;
    },
    onSuccess: (result, operation) => {
      if (
        !isCurrentOperation(
          operation,
          operationGeneration,
          selectedCompanyIdRef,
        )
      ) {
        return;
      }
      if (!result.ok) {
        const error = result.error;
        if (isAmbiguous(error)) {
          setFeedback(null);
          setAttempt(operation);
          attemptRef.current = operation;
          return;
        }
        setAttempt(null);
        attemptRef.current = null;
        if (isAuthorizationFailure(error)) {
          neutralize({ message: sessionFailureMessage, severity: "error" });
          void refreshBootstrap();
          return;
        }
        setFeedback(feedbackFor(error, operation.kind));
        return;
      }

      setAttempt(null);
      attemptRef.current = null;
      setFeedback(null);
      if ("state" in result && operation.kind === "invite") {
        setInviteDialogOpen(false);
        notify(
          result.state === "created"
            ? "Convite enviado."
            : "Convite já havia sido enviado.",
        );
      } else {
        // Mutação concluída: fecha o diálogo de gestão para devolver o foco
        // à lista e notifica o desfecho pelo snackbar.
        setManageDialogMemberId(null);
        setManageDialogRoleId("");
        setManageDialogSourceId("");
        notify(successMessageFor(operation.kind));
      }
      void queryClient.invalidateQueries({
        queryKey: [membersQueryKey, operation.companyId],
      });
      void queryClient.invalidateQueries({
        queryKey: [rolesQueryKey, operation.companyId],
      });
    },
  });

  const readFailure =
    members.data && !members.data.ok
      ? members.data.error
      : roles.data && !roles.data.ok
        ? roles.data.error
        : null;

  useEffect(() => {
    if (!readFailure) return;
    const failureKey = selectedCompanyIdRef.current + ":" + readFailure.kind;
    if (processedReadFailure.current === failureKey) return;
    processedReadFailure.current = failureKey;

    if (isAuthorizationFailure(readFailure)) {
      neutralize({ message: sessionFailureMessage, severity: "error" });
      void refreshBootstrap();
    }
  }, [neutralize, readFailure, refreshBootstrap]);

  const hasDefinitiveBootstrap = Boolean(bootstrap) || status !== "pending";
  useEffect(() => {
    if (
      !hasDefinitiveBootstrap ||
      selectedCompanyId === "" ||
      eligibleCompanies.some(
        (company) => company.companyId === selectedCompanyId,
      )
    ) {
      return;
    }
    neutralize(null);
  }, [
    eligibleCompanies,
    hasDefinitiveBootstrap,
    neutralize,
    selectedCompanyId,
  ]);

  useEffect(() => {
    if (!inviteDialogOpen) inviteForm.reset();
  }, [inviteDialogOpen, inviteForm]);

  const locked = Boolean(attempt) || mutation.isPending;

  const beginOperation = (operation: PermissionsOperation) => {
    if (!selectedCompany || attemptRef.current || inFlight.current) return;
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate(operation);
  };

  const beginAssign = (member: PermissionsMember, role: PermissionsRole) => {
    if (!selectedCompany) return;
    beginOperation({
      command: {
        companyId: selectedCompany.companyId,
        membershipId: member.membershipId,
        requestId: createRequestId(),
        roleId: role.roleId,
      },
      companyId: selectedCompany.companyId,
      generation: operationGeneration.current,
      kind: "assign",
    });
  };

  const beginUnassign = (
    member: PermissionsMember,
    role: PermissionsMember["roles"][number],
  ) => {
    if (!selectedCompany) return;
    beginOperation({
      command: {
        companyId: selectedCompany.companyId,
        membershipId: member.membershipId,
        requestId: createRequestId(),
        roleId: role.roleId,
      },
      companyId: selectedCompany.companyId,
      generation: operationGeneration.current,
      kind: "unassign",
    });
  };

  const beginCopy = (
    targetMember: PermissionsMember,
    sourceMemberId: string,
  ) => {
    if (!selectedCompany) return;
    beginOperation({
      command: {
        companyId: selectedCompany.companyId,
        requestId: createRequestId(),
        sourceMembershipId: sourceMemberId,
        targetMembershipId: targetMember.membershipId,
      },
      companyId: selectedCompany.companyId,
      generation: operationGeneration.current,
      kind: "copy",
    });
  };

  const beginInvite = (values: InviteForm) => {
    if (!selectedCompany) return;
    beginOperation({
      command: {
        companyId: selectedCompany.companyId,
        displayName: values.displayName.trim(),
        email: values.email.trim(),
        requestId: createRequestId(),
      },
      companyId: selectedCompany.companyId,
      generation: operationGeneration.current,
      kind: "invite",
    });
  };

  const retry = () => {
    const pendingAttempt = attemptRef.current;
    if (
      !pendingAttempt ||
      inFlight.current ||
      !isCurrentOperation(
        pendingAttempt,
        operationGeneration,
        selectedCompanyIdRef,
      )
    ) {
      return;
    }
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate(pendingAttempt);
  };

  if (!bootstrap) {
    return <CircularProgress aria-label="Carregando permissões" />;
  }

  if (eligibleCompanies.length === 0) {
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Gerencie papéis e acessos dos membros da empresa."
          eyebrow="Administração"
          title="Permissões"
        />
        <Alert severity="info">
          Nenhuma empresa disponível para gerenciar permissões.
        </Alert>
      </Stack>
    );
  }

  const memberItems = members.data?.ok ? members.data.items : [];
  const roleItems = roles.data?.ok ? roles.data.items : [];
  const normalizedSearch = normalizeSearchText(memberSearch);
  const visibleMembers =
    normalizedSearch === ""
      ? memberItems
      : memberItems.filter((member) =>
          normalizeSearchText(member.displayName).includes(normalizedSearch),
        );
  const adminCount = memberItems.filter((member) =>
    member.capabilities.includes(permissionsCapability),
  ).length;
  const manageDialogMember = memberItems.find(
    (member) => member.membershipId === manageDialogMemberId,
  );
  const manageDialogRole = roleItems.find(
    (role) => role.roleId === manageDialogRoleId,
  );
  const manageDialogSource = memberItems.find(
    (member) => member.membershipId === manageDialogSourceId,
  );
  const membersFailed =
    (members.data !== undefined && !members.data.ok) || members.isError;
  const rolesFailed =
    (roles.data !== undefined && !roles.data.ok) || roles.isError;

  const attemptNotice = attempt ? (
    <Alert severity="warning">{attemptWarningMessage}</Alert>
  ) : null;

  return (
    <Stack spacing={3}>
      <PageHeader
        actions={
          <Button
            disabled={!selectedCompany || locked}
            onClick={() => setInviteDialogOpen(true)}
            variant="contained"
          >
            Convidar membro
          </Button>
        }
        description="Gerencie papéis e acessos dos membros da empresa."
        eyebrow="Administração"
        title="Permissões"
      />
      <Card component="section" variant="outlined">
        <CardContent>
          <Stack spacing={1}>
            <Typography component="h2" variant="h6">
              Empresa
            </Typography>
            <TextField
              label="Empresa"
              onChange={(event) => selectCompany(event.target.value)}
              select
              sx={{ maxWidth: "24rem" }}
              value={selectedCompany?.companyId ?? ""}
            >
              <MenuItem value="">Selecione uma empresa</MenuItem>
              {eligibleCompanies.map((company) => (
                <MenuItem key={company.companyId} value={company.companyId}>
                  {company.companyName}
                </MenuItem>
              ))}
            </TextField>
            {members.data?.ok ? (
              <Typography color="text.secondary">
                {memberItems.length}{" "}
                {memberItems.length === 1 ? "membro" : "membros"} ·{" "}
                {adminCount}{" "}
                {adminCount === 1 ? "administrador" : "administradores"}
              </Typography>
            ) : null}
          </Stack>
        </CardContent>
      </Card>
      {feedback && !attempt && !manageDialogMember && !inviteDialogOpen ? (
        <Alert role="alert" severity={feedback.severity}>
          {feedback.message}
        </Alert>
      ) : null}
      {attempt && !manageDialogMember && !inviteDialogOpen ? (
        <Alert
          action={
            <Button
              color="inherit"
              onClick={retry}
              size="small"
              variant="outlined"
            >
              Tentar novamente
            </Button>
          }
          severity="warning"
        >
          {attemptWarningMessage}
        </Alert>
      ) : null}
      {selectedCompany ? (
        <Card aria-label="Membros" component="section" variant="outlined">
          <CardContent>
            <Stack spacing={2}>
              <Stack
                direction="row"
                spacing={2}
                sx={{
                  alignItems: "center",
                  flexWrap: "wrap",
                  justifyContent: "space-between",
                  rowGap: 1,
                }}
              >
                <Typography component="h2" variant="h6">
                  Membros
                </Typography>
                <TextField
                  disabled={!members.data?.ok}
                  label="Buscar membro"
                  onChange={(event) => setMemberSearch(event.target.value)}
                  size="small"
                  sx={{ maxWidth: "20rem" }}
                  value={memberSearch}
                />
              </Stack>
              {members.isPending ? (
                <Stack
                  aria-label="Carregando membros"
                  role="status"
                  spacing={1}
                >
                  {[0, 1, 2].map((index) => (
                    <Skeleton
                      aria-hidden="true"
                      key={index}
                      height={44}
                      variant="rounded"
                    />
                  ))}
                </Stack>
              ) : null}
              {membersFailed ? (
                <Alert
                  role="alert"
                  severity="error"
                  action={
                    <Button
                      color="inherit"
                      onClick={() => void members.refetch()}
                      size="small"
                    >
                      Tentar novamente
                    </Button>
                  }
                >
                  Não foi possível carregar os membros.
                </Alert>
              ) : null}
              {members.data?.ok && memberItems.length === 0 ? (
                <Typography color="text.secondary">
                  Nenhum membro na empresa.
                </Typography>
              ) : null}
              {members.data?.ok &&
              memberItems.length > 0 &&
              visibleMembers.length === 0 ? (
                <Typography color="text.secondary">
                  Nenhum membro corresponde à busca.
                </Typography>
              ) : null}
              {visibleMembers.length > 0 ? (
                <List disablePadding>
                  {visibleMembers.map((member, index) => (
                    <ListItem
                      disableGutters
                      divider={index < visibleMembers.length - 1}
                      key={member.membershipId}
                      sx={{ display: "block", py: 1.5 }}
                    >
                      <Stack
                        direction={{ xs: "column", sm: "row" }}
                        spacing={1}
                        sx={{
                          alignItems: { sm: "center" },
                          justifyContent: "space-between",
                        }}
                      >
                        <Stack spacing={0.5} sx={{ minWidth: 0 }}>
                          <Typography>{member.displayName}</Typography>
                          <Stack
                            direction="row"
                            spacing={1}
                            sx={{
                              alignItems: "center",
                              flexWrap: "wrap",
                              rowGap: 0.5,
                            }}
                          >
                            {member.roles.map((role) => (
                              <Chip
                                aria-label={
                                  "Remover papel " +
                                  role.name +
                                  " de " +
                                  member.displayName
                                }
                                key={role.roleId}
                                label={role.name}
                                onDelete={
                                  locked
                                    ? undefined
                                    : () => beginUnassign(member, role)
                                }
                                size="small"
                              />
                            ))}
                            {member.invitationStatus ? (
                              <Chip
                                color={
                                  invitationChipByStatus[
                                    member.invitationStatus
                                  ].warning
                                    ? "warning"
                                    : "default"
                                }
                                key={member.membershipId + "-invitation"}
                                label={
                                  invitationChipByStatus[
                                    member.invitationStatus
                                  ].label
                                }
                                size="small"
                                variant="outlined"
                              />
                            ) : null}
                          </Stack>
                        </Stack>
                        <Button
                          aria-label={
                            "Gerenciar acesso de " + member.displayName
                          }
                          onClick={() => {
                            setManageDialogMemberId(member.membershipId);
                            setManageDialogRoleId("");
                            setManageDialogSourceId("");
                          }}
                          size="small"
                          variant="outlined"
                        >
                          Gerenciar acesso
                        </Button>
                      </Stack>
                    </ListItem>
                  ))}
                </List>
              ) : null}
            </Stack>
          </CardContent>
        </Card>
      ) : null}
      {manageDialogMember ? (
        <Dialog onClose={closeManageDialog} open={manageDialogMember !== null}>
          <DialogTitle>
            Gerenciar acesso de {manageDialogMember.displayName}
          </DialogTitle>
          <DialogContent>
            <Stack aria-busy={mutation.isPending} spacing={2} sx={{ pt: 1 }}>
              {feedback && !attempt ? (
                <Alert role="alert" severity={feedback.severity}>
                  {feedback.message}
                </Alert>
              ) : null}
              {attemptNotice}
              <Stack spacing={1}>
                <Typography component="h3" variant="subtitle2">
                  Papéis atuais
                </Typography>
                {manageDialogMember.roles.length === 0 ? (
                  <Typography color="text.secondary">
                    Nenhum papel atribuído.
                  </Typography>
                ) : (
                  <Stack
                    direction="row"
                    spacing={1}
                    sx={{
                      alignItems: "center",
                      flexWrap: "wrap",
                      rowGap: 0.5,
                    }}
                  >
                    {manageDialogMember.roles.map((role) => (
                      <Chip
                        aria-label={
                          "Remover papel " +
                          role.name +
                          " de " +
                          manageDialogMember.displayName
                        }
                        key={role.roleId}
                        label={role.name}
                        onDelete={
                          locked
                            ? undefined
                            : () => beginUnassign(manageDialogMember, role)
                        }
                        size="small"
                      />
                    ))}
                  </Stack>
                )}
              </Stack>
              {rolesFailed ? (
                <Alert role="alert" severity="error">
                  Não foi possível carregar os papéis.
                </Alert>
              ) : null}
              {assignableRoles(roleItems, manageDialogMember).length === 0 ? (
                <Typography color="text.secondary">
                  Todos os papéis já estão atribuídos.
                </Typography>
              ) : (
                <FormControl disabled={locked} fullWidth>
                  <InputLabel id="permissions-assign-role-label">
                    Adicionar papel
                  </InputLabel>
                  <Select
                    aria-describedby="permissions-assign-role-helper"
                    id="permissions-assign-role"
                    label="Adicionar papel"
                    labelId="permissions-assign-role-label"
                    onChange={(event) =>
                      setManageDialogRoleId(event.target.value)
                    }
                    value={manageDialogRoleId}
                  >
                    {assignableRoles(roleItems, manageDialogMember).map(
                      (role) => (
                        <MenuItem key={role.roleId} value={role.roleId}>
                          {role.name}
                        </MenuItem>
                      ),
                    )}
                  </Select>
                  <FormHelperText id="permissions-assign-role-helper">
                    A atribuição vale para toda a empresa.
                  </FormHelperText>
                </FormControl>
              )}
              <FormControl disabled={locked} fullWidth>
                <InputLabel id="permissions-copy-source-label">
                  Copiar papéis de
                </InputLabel>
                <Select
                  id="permissions-copy-source"
                  label="Copiar papéis de"
                  labelId="permissions-copy-source-label"
                  onChange={(event) =>
                    setManageDialogSourceId(event.target.value)
                  }
                  value={manageDialogSourceId}
                >
                  {memberItems
                    .filter(
                      (member) =>
                        member.membershipId !==
                        manageDialogMember.membershipId,
                    )
                    .map((member) => (
                      <MenuItem
                        key={member.membershipId}
                        value={member.membershipId}
                      >
                        {member.displayName}
                      </MenuItem>
                    ))}
                </Select>
                <FormHelperText>
                  Os papéis atuais serão substituídos pelos papéis do membro
                  escolhido.
                </FormHelperText>
              </FormControl>
              {mutation.isPending ? (
                <Stack
                  direction="row"
                  role="status"
                  spacing={1}
                  sx={{ alignItems: "center" }}
                >
                  <CircularProgress
                    aria-label="Aplicando alteração de acesso"
                    size={20}
                  />
                  Aplicando alteração…
                </Stack>
              ) : null}
            </Stack>
          </DialogContent>
          <DialogActions>
            {attempt && !mutation.isPending ? (
              <Button onClick={retry} variant="outlined">
                Tentar novamente
              </Button>
            ) : null}
            <Button onClick={closeManageDialog}>Fechar</Button>
            <Button
              disabled={!manageDialogRole || locked}
              onClick={() =>
                manageDialogMember &&
                manageDialogRole &&
                beginAssign(manageDialogMember, manageDialogRole)
              }
              variant="contained"
            >
              Atribuir papel
            </Button>
            <Button
              disabled={!manageDialogSource || locked}
              onClick={() =>
                manageDialogMember &&
                manageDialogSource &&
                beginCopy(manageDialogMember, manageDialogSource.membershipId)
              }
              variant="outlined"
            >
              Copiar papéis
            </Button>
          </DialogActions>
        </Dialog>
      ) : null}
      <Dialog onClose={closeInviteDialog} open={inviteDialogOpen}>
        <DialogTitle>Convidar membro</DialogTitle>
        <DialogContent>
          <Stack
            aria-busy={mutation.isPending}
            component="form"
            id="permissions-invite-form"
            noValidate
            onSubmit={inviteForm.handleSubmit(beginInvite)}
            spacing={2}
            sx={{ pt: 1 }}
          >
            {feedback && !attempt ? (
              <Alert role="alert" severity={feedback.severity}>
                {feedback.message}
              </Alert>
            ) : null}
            {attemptNotice}
            <TextField
              {...inviteForm.register("displayName")}
              disabled={locked}
              error={Boolean(inviteForm.formState.errors.displayName)}
              helperText={inviteForm.formState.errors.displayName?.message}
              label="Nome"
            />
            <TextField
              {...inviteForm.register("email")}
              disabled={locked}
              error={Boolean(inviteForm.formState.errors.email)}
              helperText={inviteForm.formState.errors.email?.message}
              label="E-mail"
              type="email"
            />
            {mutation.isPending ? (
              <Stack
                direction="row"
                role="status"
                spacing={1}
                sx={{ alignItems: "center" }}
              >
                <CircularProgress aria-label="Enviando convite" size={20} />
                Enviando convite…
              </Stack>
            ) : null}
          </Stack>
        </DialogContent>
        <DialogActions>
          {attempt && !mutation.isPending ? (
            <Button onClick={retry} variant="outlined">
              Tentar novamente
            </Button>
          ) : null}
          <Button onClick={closeInviteDialog}>Cancelar</Button>
          <Button
            disabled={locked}
            form="permissions-invite-form"
            type="submit"
            variant="contained"
          >
            Convidar
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  );
}

function assignableRoles(
  roles: readonly PermissionsRole[],
  member: PermissionsMember,
): PermissionsRole[] {
  return roles.filter(
    (role) =>
      !member.roles.some((memberRole) => memberRole.roleId === role.roleId),
  );
}

// Busca amigável para nomes em pt-BR: remove acentos e compara sem caixa,
// para que quem busca "andre" encontre "André" e vice-versa.
function normalizeSearchText(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("pt-BR")
    .trim();
}

function isAuthorizationFailure(failure: PermissionsFailure): boolean {
  return failure.kind === "unauthorized" || failure.kind === "forbidden";
}

function isAmbiguous(failure: PermissionsFailure): boolean {
  // Somente falhas sinalizadas com mayHaveCommitted podem ter chegado ao
  // servidor; falhas de antiforgery ou de validação local são definitivas.
  return failure.mayHaveCommitted === true;
}

function isCurrentOperation(
  operation: PermissionsOperation,
  operationGeneration: MutableRefObject<number>,
  selectedCompanyId: MutableRefObject<string>,
): boolean {
  return (
    operation.generation === operationGeneration.current &&
    operation.companyId === selectedCompanyId.current
  );
}

function successMessageFor(kind: MutationKind): string {
  switch (kind) {
    case "assign":
      return "Papel atribuído.";
    case "unassign":
      return "Papel removido.";
    case "copy":
      return "Permissões copiadas.";
    case "invite":
      return "Convite enviado.";
  }
}

function feedbackFor(
  failure: PermissionsFailure,
  kind: MutationKind,
): PageFeedback {
  if (failure.kind === "last_admin_guard") {
    return { message: lastAdminGuardMessage, severity: "error" };
  }
  if (failure.kind === "member_conflict") {
    return { message: memberConflictMessage, severity: "error" };
  }
  if (failure.kind === "rate_limited") {
    return { message: rateLimitedMessage, severity: "error" };
  }
  if (
    failure.kind === "idempotency_conflict" ||
    failure.kind === "integrity_conflict"
  ) {
    return { message: conflictMessage, severity: "error" };
  }
  return { message: genericFailureMessage(kind), severity: "error" };
}

function genericFailureMessage(kind: MutationKind): string {
  switch (kind) {
    case "assign":
      return "Não foi possível atribuir o papel.";
    case "unassign":
      return "Não foi possível remover o papel.";
    case "copy":
      return "Não foi possível copiar as permissões.";
    case "invite":
      return "Não foi possível enviar o convite.";
  }
}
