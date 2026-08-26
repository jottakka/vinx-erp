import { zodResolver } from "@hookform/resolvers/zod";
import PersonAddAltOutlinedIcon from "@mui/icons-material/PersonAddAltOutlined";
import PersonOutlinedIcon from "@mui/icons-material/PersonOutlined";
import {
  Alert,
  Button,
  Card,
  CardContent,
  CircularProgress,
  IconButton,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useMutation, useQueries, useQueryClient } from "@tanstack/react-query";
import type { MutableRefObject, ReactElement } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { AuthorizedCompanyFilter } from "../../shared/ui/authorized-company-filter";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { CompanySelect } from "../../shared/ui/company-select";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { hasCompanyCapability } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  createEmployeeDirectoryClient,
  type EmployeeDirectoryCreateCommand,
  type EmployeeDirectoryFailure,
  type EmployeeDirectoryGateway,
  type EmployeeDirectoryItem,
} from "./employee-directory-client";
import { EmployeeProfileDialog } from "./employee-profile-dialog";

const employeeCapability = "foundation.employees.create";
const employeeFormSchema = z.object({
  displayName: z.string().trim().min(1, "Informe um nome válido.").max(200),
  email: z
    .string()
    .trim()
    .min(1, "Informe um e-mail válido.")
    .max(320)
    .email("Informe um e-mail válido."),
});
type EmployeeForm = z.infer<typeof employeeFormSchema>;
type PeoplePageProps = {
  gateway?: EmployeeDirectoryGateway;
  createRequestId?: () => string;
};
type Feedback = "success" | "error" | null;
type DirectoryOperation = {
  command: EmployeeDirectoryCreateCommand;
  companyId: string;
  generation: number;
  queryKey: readonly ["employee-directory", string, string];
  userId: string;
};
type ConfirmedOperation = Omit<DirectoryOperation, "command">;
type RefreshReconciliation = {
  operation: ConfirmedOperation;
  phase: "accepted" | "pending" | "rejected";
  purpose: "authorization" | "creation" | "recovery";
};
type DirectoryRow = EmployeeDirectoryItem & {
  company: { companyId: string; companyName: string };
};

export function PeoplePage(props: PeoplePageProps): ReactElement {
  const { bootstrap, isSigningOut } = useSession();
  const lastUserId = useRef<string | null>(null);
  if (bootstrap?.user.id) lastUserId.current = bootstrap.user.id;
  const ownerKey = isSigningOut
    ? "signed-out"
    : (bootstrap?.user.id ?? lastUserId.current ?? "anonymous");
  return <PeoplePageContent key={ownerKey} {...props} />;
}

function PeoplePageContent({
  gateway = createEmployeeDirectoryClient(),
  createRequestId = () => crypto.randomUUID(),
}: PeoplePageProps): ReactElement {
  const { bootstrap, refreshBootstrap, status } = useSession();
  const queryClient = useQueryClient();
  const [selectedCompanyIds, setSelectedCompanyIds] = useState<string[]>([]);
  const [createCompanyId, setCreateCompanyId] = useState("");
  const [search, setSearch] = useState("");
  const [creationOpen, setCreationOpen] = useState(false);
  const [profile, setProfile] = useState<DirectoryRow | null>(null);
  const [attempt, setAttempt] = useState<DirectoryOperation | null>(null);
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [pendingRefresh, setPendingRefresh] =
    useState<RefreshReconciliation | null>(null);
  const [knownCreatedOperation, setKnownCreatedOperation] =
    useState<ConfirmedOperation | null>(null);
  const [isRefreshingKnownCreation, setIsRefreshingKnownCreation] =
    useState(false);
  const operationGeneration = useRef(0);
  const inFlight = useRef(false);
  const attemptRef = useRef<DirectoryOperation | null>(null);
  const processedFailures = useRef(new Set<string>());
  const authorizationRefreshInFlight = useRef(false);
  const refreshGate = useRef(false);
  const currentUserIdRef = useRef<string | null>(null);
  const viewOwnerUserId = useRef<string | null>(null);
  const createCompanyIdRef = useRef("");
  const form = useForm<EmployeeForm>({
    defaultValues: { displayName: "", email: "" },
    resolver: zodResolver(employeeFormSchema),
  });
  const eligibleCompanies = useMemo(
    () =>
      (bootstrap?.companies ?? []).filter((company) =>
        hasCompanyCapability(company, employeeCapability),
      ),
    [bootstrap],
  );
  const eligibleIds = useMemo(
    () => new Set(eligibleCompanies.map((company) => company.companyId)),
    [eligibleCompanies],
  );
  const concreteCompanyIds = useMemo(() => {
    const selected = selectedCompanyIds.filter((id) => eligibleIds.has(id));
    return selected.length
      ? selected
      : eligibleCompanies.map((company) => company.companyId);
  }, [eligibleCompanies, eligibleIds, selectedCompanyIds]);
  const listQueries = useQueries({
    queries: concreteCompanyIds.map((companyId) => ({
      enabled:
        Boolean(bootstrap?.user.id) &&
        !knownCreatedOperation &&
        !refreshGate.current,
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        gateway.list(companyId, signal),
      queryKey: [
        "employee-directory",
        bootstrap?.user.id ?? "none",
        companyId,
      ] as const,
      retry: false,
    })),
  });
  const userId = bootstrap?.user.id;
  currentUserIdRef.current = userId ?? null;
  createCompanyIdRef.current = createCompanyId;

  useEffect(() => {
    if (!userId) return;
    if (viewOwnerUserId.current && viewOwnerUserId.current !== userId) {
      operationGeneration.current += 1;
      setSelectedCompanyIds([]);
      setCreateCompanyId("");
      setCreationOpen(false);
      form.reset();
      setAttempt(null);
      attemptRef.current = null;
      setFeedback(null);
      setProfile(null);
      setKnownCreatedOperation(null);
      setPendingRefresh(null);
      refreshGate.current = false;
      authorizationRefreshInFlight.current = false;
    }
    viewOwnerUserId.current = userId;
  }, [form, userId]);

  useEffect(() => {
    if (!bootstrap) return;
    const valid = selectedCompanyIds.filter((id) => eligibleIds.has(id));
    if (valid.length !== selectedCompanyIds.length)
      setSelectedCompanyIds(valid);
    if (createCompanyId && !eligibleIds.has(createCompanyId)) {
      operationGeneration.current += 1;
      setCreateCompanyId("");
      form.reset();
      setAttempt(null);
      attemptRef.current = null;
      setFeedback(null);
    }
  }, [bootstrap, createCompanyId, eligibleIds, form, selectedCompanyIds]);

  const beginAuthorizationReconciliation = useCallback(
    async (operation: ConfirmedOperation) => {
      if (authorizationRefreshInFlight.current) return;
      authorizationRefreshInFlight.current = true;
      refreshGate.current = true;
      queryClient.removeQueries({ queryKey: operation.queryKey });
      setPendingRefresh({
        operation,
        phase: "pending",
        purpose: "authorization",
      });
      const accepted = await refreshBootstrap();
      setPendingRefresh({
        operation,
        phase: accepted ? "accepted" : "rejected",
        purpose: "authorization",
      });
    },
    [queryClient, refreshBootstrap],
  );

  useEffect(() => {
    listQueries.forEach((query, index) => {
      const result = query.data;
      if (!result || result.ok || !isAuthorizationFailure(result.error)) return;
      const companyId = concreteCompanyIds[index];
      if (
        !companyId ||
        processedFailures.current.has(`${companyId}:${result.error.kind}`)
      )
        return;
      processedFailures.current.add(`${companyId}:${result.error.kind}`);
      if (userId)
        void beginAuthorizationReconciliation({
          companyId,
          generation: operationGeneration.current,
          queryKey: ["employee-directory", userId, companyId],
          userId,
        });
    });
  }, [
    beginAuthorizationReconciliation,
    concreteCompanyIds,
    listQueries,
    userId,
  ]);

  useEffect(() => {
    if (!pendingRefresh || pendingRefresh.phase === "pending") return;
    const current = pendingRefresh.operation;
    if (pendingRefresh.purpose === "authorization") {
      operationGeneration.current += 1;
      setSelectedCompanyIds((ids) => ids.filter((id) => eligibleIds.has(id)));
      setAttempt(null);
      attemptRef.current = null;
      setProfile(null);
      setFeedback(null);
      authorizationRefreshInFlight.current = false;
      refreshGate.current = false;
      setPendingRefresh(null);
      return;
    }
    if (pendingRefresh.phase === "rejected") {
      if (
        pendingRefresh.purpose === "creation" &&
        current.generation === operationGeneration.current
      ) {
        form.reset();
        setKnownCreatedOperation(current);
      }
      refreshGate.current = false;
      setPendingRefresh(null);
      return;
    }
    if (
      isCurrentEligibleOperation(
        current,
        operationGeneration,
        createCompanyIdRef,
        userId,
      )
    ) {
      if (pendingRefresh.purpose === "creation") {
        form.reset();
        setFeedback("success");
        setCreationOpen(false);
      }
      setKnownCreatedOperation(null);
      void queryClient.invalidateQueries({ queryKey: current.queryKey });
    } else if (pendingRefresh.purpose === "recovery")
      setKnownCreatedOperation(null);
    refreshGate.current = false;
    setPendingRefresh(null);
  }, [eligibleIds, form, pendingRefresh, queryClient, userId]);

  const mutation = useMutation({
    mutationFn: (operation: DirectoryOperation) =>
      gateway.create(operation.command),
    onSettled: () => {
      inFlight.current = false;
    },
    onSuccess: async (result, operation) => {
      if (
        !isCurrentOperation(
          operation,
          operationGeneration,
          createCompanyIdRef,
          currentUserIdRef,
        )
      )
        return;
      if (!result.ok) {
        if (isAmbiguous(result.error)) {
          setAttempt(operation);
          attemptRef.current = operation;
          return;
        }
        if (isAuthorizationFailure(result.error)) {
          await beginAuthorizationReconciliation(
            toConfirmedOperation(operation),
          );
          return;
        }
        setFeedback("error");
        return;
      }
      const confirmed = toConfirmedOperation(operation);
      refreshGate.current = true;
      setPendingRefresh({
        operation: confirmed,
        phase: "pending",
        purpose: "creation",
      });
      const accepted = await refreshBootstrap();
      if (!accepted) {
        if (
          isCurrentOperation(
            operation,
            operationGeneration,
            createCompanyIdRef,
            currentUserIdRef,
          )
        ) {
          form.reset();
          setKnownCreatedOperation(confirmed);
        }
        refreshGate.current = false;
        setPendingRefresh(null);
        return;
      }
      setPendingRefresh({
        operation: confirmed,
        phase: "accepted",
        purpose: "creation",
      });
    },
  });

  const resetCreation = () => {
    operationGeneration.current += 1;
    form.reset();
    setFeedback(null);
    setAttempt(null);
    attemptRef.current = null;
    setCreateCompanyId("");
  };
  const changeCreateCompany = (companyId: string) => {
    if (companyId !== createCompanyId) {
      operationGeneration.current += 1;
      form.reset();
      setAttempt(null);
      attemptRef.current = null;
      setFeedback(null);
    }
    setCreateCompanyId(companyId);
  };
  const openCreation = () => {
    resetCreation();
    setCreationOpen(true);
  };
  const reserve = (values: EmployeeForm) => {
    if (
      !createCompanyId ||
      !userId ||
      knownCreatedOperation ||
      attemptRef.current ||
      pendingRefresh ||
      mutation.isPending ||
      inFlight.current
    )
      return;
    const command = {
      companyId: createCompanyId,
      displayName: values.displayName.trim(),
      email: values.email.trim(),
      requestId: createRequestId(),
    };
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate({
      command,
      companyId: createCompanyId,
      generation: operationGeneration.current,
      queryKey: ["employee-directory", userId, createCompanyId],
      userId,
    });
  };
  const retry = () => {
    const operation = attemptRef.current;
    if (
      !operation ||
      inFlight.current ||
      !isCurrentOperation(
        operation,
        operationGeneration,
        createCompanyIdRef,
        currentUserIdRef,
      )
    )
      return;
    inFlight.current = true;
    mutation.mutate(operation);
  };
  const retryQueries = () => {
    listQueries
      .filter((query) => query.isError || (query.data && !query.data.ok))
      .forEach((query) => void query.refetch());
  };

  if (
    pendingRefresh?.purpose === "authorization" &&
    pendingRefresh.phase === "pending"
  )
    return <CircularProgress aria-label="Atualizando pessoas" />;
  if (knownCreatedOperation)
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Atualize a sessão antes de consultar novamente os dados da empresa."
          eyebrow="Pessoas"
          title="Pessoas"
        />
        <Alert severity="info">
          O cadastro foi concluído, mas a sessão precisa ser atualizada.
        </Alert>
        <Button
          disabled={isRefreshingKnownCreation}
          onClick={() => void refreshKnownCreation()}
          variant="contained"
        >
          Atualizar sessão
        </Button>
      </Stack>
    );
  if (!bootstrap)
    return (
      <CircularProgress
        aria-label={
          status === "pending" ? "Carregando pessoas" : "Atualizando pessoas"
        }
      />
    );
  if (eligibleCompanies.length === 0)
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Consulte pessoas das empresas disponíveis para sua sessão."
          eyebrow="Pessoas"
          title="Pessoas"
        />
        <Alert severity="info">
          Nenhuma empresa está disponível para esta ação.
        </Alert>
      </Stack>
    );

  const normalizedSearch = search.trim().toLocaleLowerCase();
  const rows = listQueries.flatMap((query, index): DirectoryRow[] => {
    const result = query.data;
    const company = eligibleCompanies.find(
      (item) => item.companyId === concreteCompanyIds[index],
    );
    if (!result?.ok || !company) return [];
    return result.items.map((employee) => ({ ...employee, company }));
  });
  const visibleRows = rows
    .filter((row) =>
      row.displayName.trim().toLocaleLowerCase().includes(normalizedSearch),
    )
    .sort(
      (left, right) =>
        left.displayName
          .trim()
          .localeCompare(right.displayName.trim(), "pt-BR", {
            sensitivity: "base",
          }) ||
        left.company.companyName.localeCompare(
          right.company.companyName,
          "pt-BR",
          { sensitivity: "base" },
        ) ||
        left.employeeId.localeCompare(right.employeeId) ||
        left.membershipId.localeCompare(right.membershipId),
    );
  const isLoading = listQueries.some((query) => query.isPending);
  const failures = listQueries.filter(
    (query) => query.data && !query.data.ok,
  ).length;
  const selectedCreateCompany = eligibleCompanies.find(
    (company) => company.companyId === createCompanyId,
  );

  return (
    <Stack spacing={3}>
      <PageHeader
        description="Consulte pessoas por empresa e cadastre funcionários sem conceder acesso ao sistema."
        eyebrow="Pessoas"
        title="Pessoas"
      />
      <Card component="section" variant="outlined">
        <CardContent>
          <Stack spacing={2}>
            <AuthorizedCompanyFilter
              companies={eligibleCompanies}
              label="Empresas"
              onChange={setSelectedCompanyIds}
              selectedCompanyIds={selectedCompanyIds}
            />
            <TextField
              label="Buscar por nome"
              onChange={(event) => setSearch(event.target.value)}
              value={search}
            />
            <Stack
              direction={{ sm: "row", xs: "column" }}
              spacing={1}
              sx={{
                alignItems: { sm: "center", xs: "stretch" },
                justifyContent: "space-between",
              }}
            >
              <Typography color="text.secondary" variant="body2">
                {visibleRows.length} funcionário
                {visibleRows.length === 1 ? "" : "s"}
              </Typography>
              {search ? (
                <IconButton
                  aria-label="Limpar busca"
                  onClick={() => setSearch("")}
                  size="small"
                >
                  <Typography variant="caption">Limpar</Typography>
                </IconButton>
              ) : null}
              <Button
                onClick={openCreation}
                startIcon={<PersonAddAltOutlinedIcon />}
                variant="contained"
              >
                Cadastrar funcionário
              </Button>
            </Stack>
          </Stack>
        </CardContent>
      </Card>
      {feedback === "success" ? (
        <Alert severity="success">Funcionário cadastrado.</Alert>
      ) : null}
      <Card component="section" variant="outlined">
        <CardContent>
          <Stack spacing={1}>
            <Typography component="h2" variant="h6">
              Funcionários
            </Typography>
            {isLoading ? (
              <CircularProgress
                aria-label="Carregando funcionários"
                size={24}
              />
            ) : null}
            {failures ? (
              <Alert severity="error">
                <Stack
                  direction="row"
                  spacing={1}
                  sx={{
                    alignItems: "center",
                    justifyContent: "space-between",
                  }}
                >
                  <span>Não foi possível carregar parte das pessoas.</span>
                  <Button onClick={retryQueries} size="small">
                    Tentar novamente
                  </Button>
                </Stack>
              </Alert>
            ) : null}
            {!isLoading && !failures && !rows.length ? (
              <Typography color="text.secondary">
                Nenhum funcionário reservado.
              </Typography>
            ) : null}
            {!isLoading && rows.length && !visibleRows.length ? (
              <Typography color="text.secondary">
                Nenhum funcionário corresponde à busca.
              </Typography>
            ) : null}
            {visibleRows.map((row) => (
              <Button
                key={`${row.company.companyId}:${row.employeeId}`}
                onClick={() => setProfile(row)}
                startIcon={<PersonOutlinedIcon />}
                sx={{
                  justifyContent: "flex-start",
                  textAlign: "left",
                  width: "100%",
                }}
                variant="outlined"
              >
                <Stack spacing={0.25} sx={{ alignItems: "flex-start" }}>
                  <Typography>{row.displayName}</Typography>
                  <CompanyIdentity
                    companyId={row.company.companyId}
                    companyName={row.company.companyName}
                  />
                </Stack>
              </Button>
            ))}
          </Stack>
        </CardContent>
      </Card>
      <EntityDialog
        description="Este cadastro não envia convite nem concede acesso ao sistema."
        onClose={() => {
          setCreationOpen(false);
          resetCreation();
        }}
        open={creationOpen}
        title="Cadastrar funcionário"
      >
        <Stack
          component="form"
          noValidate
          onSubmit={form.handleSubmit(reserve)}
          spacing={2}
        >
          <CompanySelect
            companies={eligibleCompanies}
            label="Empresa"
            onChange={changeCreateCompany}
            value={createCompanyId}
          />
          <TextField
            {...form.register("displayName")}
            error={Boolean(form.formState.errors.displayName)}
            helperText={form.formState.errors.displayName?.message}
            label="Nome"
          />
          <TextField
            {...form.register("email")}
            error={Boolean(form.formState.errors.email)}
            helperText={form.formState.errors.email?.message}
            label="E-mail"
            type="email"
          />
          <Button
            disabled={
              !selectedCreateCompany ||
              Boolean(attempt) ||
              Boolean(pendingRefresh) ||
              mutation.isPending
            }
            startIcon={<PersonAddAltOutlinedIcon />}
            type="submit"
            variant="contained"
          >
            Cadastrar funcionário
          </Button>
          {attempt && !mutation.isPending ? (
            <Button onClick={retry} variant="outlined">
              Tentar novamente
            </Button>
          ) : null}
          {feedback === "success" ? (
            <Alert severity="success">Funcionário cadastrado.</Alert>
          ) : null}
          {feedback === "error" ? (
            <Alert severity="error">
              Não foi possível concluir o cadastro.
            </Alert>
          ) : null}
        </Stack>
      </EntityDialog>
      <EmployeeProfileDialog
        companyId={profile?.company.companyId}
        companyName={profile?.company.companyName}
        employee={profile}
        onClose={() => setProfile(null)}
        open={Boolean(profile)}
      />
    </Stack>
  );

  async function refreshKnownCreation() {
    if (isRefreshingKnownCreation || !knownCreatedOperation) return;
    setIsRefreshingKnownCreation(true);
    const operation = knownCreatedOperation;
    refreshGate.current = true;
    setPendingRefresh({ operation, phase: "pending", purpose: "recovery" });
    const accepted = await refreshBootstrap();
    setPendingRefresh({
      operation,
      phase: accepted ? "accepted" : "rejected",
      purpose: "recovery",
    });
    setIsRefreshingKnownCreation(false);
  }
}

function isAuthorizationFailure(failure: EmployeeDirectoryFailure) {
  return failure.kind === "unauthorized" || failure.kind === "forbidden";
}
function isAmbiguous(failure: EmployeeDirectoryFailure) {
  return (
    failure.kind === "network" ||
    failure.kind === "unavailable" ||
    failure.mayHaveCommitted === true
  );
}
function isCurrentOperation(
  operation: DirectoryOperation,
  generation: MutableRefObject<number>,
  companyId: MutableRefObject<string>,
  userId: MutableRefObject<string | null>,
) {
  return (
    operation.generation === generation.current &&
    operation.companyId === companyId.current &&
    operation.userId === userId.current
  );
}
function isCurrentEligibleOperation(
  operation: ConfirmedOperation,
  generation: MutableRefObject<number>,
  companyId: MutableRefObject<string>,
  userId: string | undefined,
) {
  return (
    operation.generation === generation.current &&
    operation.companyId === companyId.current &&
    operation.userId === userId
  );
}
function toConfirmedOperation(
  operation: DirectoryOperation,
): ConfirmedOperation {
  return {
    companyId: operation.companyId,
    generation: operation.generation,
    queryKey: operation.queryKey,
    userId: operation.userId,
  };
}
