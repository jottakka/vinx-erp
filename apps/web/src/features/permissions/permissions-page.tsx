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
  const [rolesDialogMemberId, setRolesDialogMemberId] = useState<string | null>(
    null,
  );
  const [rolesDialogRoleId, setRolesDialogRoleId] = useState("");
  const [copyDialogMemberId, setCopyDialogMemberId] = useState<string | null>(
    null,
  );
  const [copyDialogSourceId, setCopyDialogSourceId] = useState("");
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

  const closeRolesDialog = useCallback(() => {
    abandonAttempt();
    setFeedback(null);
    setRolesDialogMemberId(null);
    setRolesDialogRoleId("");
  }, [abandonAttempt]);

  const closeCopyDialog = useCallback(() => {
    abandonAttempt();
    setFeedback(null);
    setCopyDialogMemberId(null);
    setCopyDialogSourceId("");
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
    setRolesDialogMemberId(null);
    setRolesDialogRoleId("");
    setCopyDialogMemberId(null);
    setCopyDialogSourceId("");
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
    setRolesDialogMemberId(null);
    setRolesDialogRoleId("");
    setCopyDialogMemberId(null);
    setCopyDialogSourceId("");
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
        if (operation.kind === "assign" || operation.kind === "unassign") {
          setRolesDialogMemberId(null);
          setRolesDialogRoleId("");
        }
        if (operation.kind === "copy") {
          setCopyDialogMemberId(null);
          setCopyDialogSourceId("");
        }
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
    const failureKey = `${selectedCompanyIdRef.current}:${readFailure.kind}`;
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
  const rolesDialogMember = memberItems.find(
    (member) => member.membershipId === rolesDialogMemberId,
  );
  const copyDialogMember = memberItems.find(
    (member) => member.membershipId === copyDialogMemberId,
  );
  const rolesDialogMemberRole = roleItems.find(
    (role) => role.roleId === rolesDialogRoleId,
  );
  const copyDialogSource = memberItems.find(
    (member) => member.membershipId === copyDialogSourceId,
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
        </CardContent>
      </Card>
      {feedback &&
      !attempt &&
      !rolesDialogMember &&
      !copyDialogMember &&
      !inviteDialogOpen ? (
        <Alert role="alert" severity={feedback.severity}>
          {feedback.message}
        </Alert>
      ) : null}
      {attempt &&
      !rolesDialogMember &&
      !copyDialogMember &&
      !inviteDialogOpen ? (
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
              <Typography component="h2" variant="h6">
                Membros
              </Typography>
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
              {members.data?.ok && memberItems.length > 0 ? (
                <List disablePadding>
                  {memberItems.map((member, index) => (
                    <ListItem
                      disableGutters
                      divider={index < memberItems.length - 1}
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
                                aria-label={`Remover papel ${role.name} de ${member.displayName}`}
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
                                key={`${member.membershipId}-invitation`}
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
                        <Stack direction="row" spacing={1}>
                          <Button
                            aria-label={`Papéis de ${member.displayName}`}
                            onClick={() => {
                              setRolesDialogMemberId(member.membershipId);
                              setRolesDialogRoleId("");
                            }}
                            size="small"
                            variant="outlined"
                          >
                            Papéis
                          </Button>
                          <Button
                            aria-label={`Copiar de outro membro para ${member.displayName}`}
                            onClick={() => {
                              setCopyDialogMemberId(member.membershipId);
                              setCopyDialogSourceId("");
                            }}
                            size="small"
                            variant="outlined"
                          >
                            Copiar de…
                          </Button>
                        </Stack>
                      </Stack>
                    </ListItem>
                  ))}
                </List>
              ) : null}
            </Stack>
          </CardContent>
        </Card>
      ) : null}
      {rolesDialogMember ? (
        <Dialog onClose={closeRolesDialog} open={rolesDialogMember !== null}>
          <DialogTitle>Papéis de {rolesDialogMember.displayName}</DialogTitle>
          <DialogContent>
            <Stack aria-busy={mutation.isPending} spacing={2} sx={{ pt: 1 }}>
              {feedback && !attempt ? (
                <Alert role="alert" severity={feedback.severity}>
                  {feedback.message}
                </Alert>
              ) : null}
              {attemptNotice}
              <Stack
                direction="row"
                spacing={1}
                sx={{ alignItems: "center", flexWrap: "wrap", rowGap: 0.5 }}
              >
                {rolesDialogMember.roles.length === 0 ? (
                  <Typography color="text.secondary">
                    Nenhum papel atribuído.
                  </Typography>
                ) : (
                  rolesDialogMember.roles.map((role) => (
                    <Chip
                      aria-label={`Remover papel ${role.name} de ${rolesDialogMember.displayName}`}
                      key={role.roleId}
                      label={role.name}
                      onDelete={
                        locked
                          ? undefined
                          : () => beginUnassign(rolesDialogMember, role)
                      }
                      size="small"
                    />
                  ))
                )}
              </Stack>
              {rolesFailed ? (
                <Alert role="alert" severity="error">
                  Não foi possível carregar os papéis.
                </Alert>
              ) : assignableRoles(roleItems, rolesDialogMember).length === 0 ? (
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
                      setRolesDialogRoleId(event.target.value)
                    }
                    value={rolesDialogRoleId}
                  >
                    {assignableRoles(roleItems, rolesDialogMember).map(
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
              {mutation.isPending ? (
                <Stack
                  direction="row"
                  role="status"
                  spacing={1}
                  sx={{ alignItems: "center" }}
                >
                  <CircularProgress
                    aria-label="Aplicando alteração de papel"
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
            <Button onClick={closeRolesDialog}>Cancelar</Button>
            <Button
              disabled={!rolesDialogMemberRole || locked}
              onClick={() =>
                rolesDialogMember &&
                rolesDialogMemberRole &&
                beginAssign(rolesDialogMember, rolesDialogMemberRole)
              }
              variant="contained"
            >
              Atribuir papel
            </Button>
          </DialogActions>
        </Dialog>
      ) : null}
      {copyDialogMember ? (
        <Dialog onClose={closeCopyDialog} open={copyDialogMember !== null}>
          <DialogTitle>
            Copiar permissões para {copyDialogMember.displayName}
          </DialogTitle>
          <DialogContent>
            <Stack aria-busy={mutation.isPending} spacing={2} sx={{ pt: 1 }}>
              {feedback && !attempt ? (
                <Alert role="alert" severity={feedback.severity}>
                  {feedback.message}
                </Alert>
              ) : null}
              {attemptNotice}
              <Typography color="text.secondary">
                Os papéis atuais serão substituídos pelos papéis do membro
                escolhido.
              </Typography>
              <FormControl disabled={locked} fullWidth>
                <InputLabel id="permissions-copy-source-label">
                  Copiar de
                </InputLabel>
                <Select
                  id="permissions-copy-source"
                  label="Copiar de"
                  labelId="permissions-copy-source-label"
                  onChange={(event) =>
                    setCopyDialogSourceId(event.target.value)
                  }
                  value={copyDialogSourceId}
                >
                  {memberItems
                    .filter(
                      (member) =>
                        member.membershipId !== copyDialogMember.membershipId,
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
              </FormControl>
              {mutation.isPending ? (
                <Stack
                  direction="row"
                  role="status"
                  spacing={1}
                  sx={{ alignItems: "center" }}
                >
                  <CircularProgress
                    aria-label="Copiando permissões"
                    size={20}
                  />
                  Copiando permissões…
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
            <Button onClick={closeCopyDialog}>Cancelar</Button>
            <Button
              disabled={!copyDialogSource || locked}
              onClick={() =>
                copyDialogMember &&
                copyDialogSource &&
                beginCopy(copyDialogMember, copyDialogSource.membershipId)
              }
              variant="contained"
            >
              Copiar permissões
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
