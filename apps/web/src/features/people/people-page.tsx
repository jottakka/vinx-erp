import { zodResolver } from "@hookform/resolvers/zod";
import PersonAddAltOutlinedIcon from "@mui/icons-material/PersonAddAltOutlined";
import PersonOutlinedIcon from "@mui/icons-material/PersonOutlined";
import {
  Alert,
  Button,
  Card,
  CardContent,
  CircularProgress,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { MutableRefObject, ReactElement } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { CompanyProvider } from "../../shared/ui/company-context";
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

export function PeoplePage({
  gateway = createEmployeeDirectoryClient(),
  createRequestId = () => crypto.randomUUID(),
}: PeoplePageProps): ReactElement {
  const { bootstrap, refreshBootstrap, status } = useSession();
  const queryClient = useQueryClient();
  const [selectedCompanyId, setSelectedCompanyId] = useState<string>("");
  const [selectedCompanyUserId, setSelectedCompanyUserId] = useState("");
  const [viewOwnerUserId, setViewOwnerUserId] = useState("");
  const [profileEmployee, setProfileEmployee] =
    useState<EmployeeDirectoryItem | null>(null);
  const [attempt, setAttempt] = useState<DirectoryOperation | null>(null);
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [pendingRefresh, setPendingRefresh] =
    useState<RefreshReconciliation | null>(null);
  const [knownCreatedOperation, setKnownCreatedOperation] =
    useState<ConfirmedOperation | null>(null);
  const [isRefreshingKnownCreation, setIsRefreshingKnownCreation] =
    useState(false);
  const processedDirectoryFailure = useRef<string | null>(null);
  const operationGeneration = useRef(0);
  const inFlight = useRef(false);
  const refreshGate = useRef(false);
  const authorizationRefreshInFlight = useRef(false);
  const attemptRef = useRef<DirectoryOperation | null>(null);
  const selectedCompanyIdRef = useRef("");
  const currentUserIdRef = useRef<string | null>(null);
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
  const selectedCompany = eligibleCompanies.find(
    (company) =>
      company.companyId === selectedCompanyId &&
      selectedCompanyUserId === bootstrap?.user.id,
  );
  const userId = bootstrap?.user.id;
  selectedCompanyIdRef.current = selectedCompany?.companyId ?? "";
  currentUserIdRef.current = userId ?? null;
  const queryKey = [
    "employee-directory",
    userId ?? "none",
    selectedCompany?.companyId ?? "none",
  ] as const;
  const directory = useQuery({
    enabled: Boolean(
      selectedCompany &&
        userId &&
        !knownCreatedOperation &&
        !refreshGate.current,
    ),
    queryFn: ({ signal }) =>
      selectedCompany
        ? gateway.list(selectedCompany.companyId, signal)
        : Promise.resolve({
            error: { kind: "invalid_request" as const },
            ok: false as const,
          }),
    queryKey,
    retry: false,
  });

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
      const refreshAccepted = await refreshBootstrap();
      setPendingRefresh({
        operation,
        phase: refreshAccepted ? "accepted" : "rejected",
        purpose: "authorization",
      });
    },
    [queryClient, refreshBootstrap],
  );

  useEffect(() => {
    if (!directory.data || directory.data.ok) return;
    const failureKey = `${selectedCompanyId}:${directory.data.error.kind}`;
    if (processedDirectoryFailure.current === failureKey) return;
    processedDirectoryFailure.current = failureKey;

    if (isAuthorizationFailure(directory.data.error)) {
      if (!selectedCompany || !userId) return;
      void beginAuthorizationReconciliation({
        companyId: selectedCompany.companyId,
        generation: operationGeneration.current,
        queryKey,
        userId,
      });
    }
  }, [
    beginAuthorizationReconciliation,
    directory.data,
    queryKey,
    selectedCompany,
    selectedCompanyId,
    userId,
  ]);

  const hasDefinitiveBootstrap = Boolean(bootstrap) || status !== "pending";
  const selectedCompanyInvalid =
    selectedCompanyId !== "" &&
    (!bootstrap ||
      selectedCompanyUserId !== userId ||
      !eligibleCompanies.some(
        (company) => company.companyId === selectedCompanyId,
      ));
  const preservesKnownCreation =
    !bootstrap &&
    ((pendingRefresh?.purpose === "creation" &&
      pendingRefresh.phase !== "accepted") ||
      knownCreatedOperation);
  const hasOwnedView = Boolean(
    selectedCompanyId ||
      viewOwnerUserId ||
      attempt ||
      feedback ||
      knownCreatedOperation ||
      pendingRefresh,
  );
  const directoryAuthorizationFailure = Boolean(
    directory.data &&
      !directory.data.ok &&
      isAuthorizationFailure(directory.data.error),
  );
  const authorizationReconciliation =
    directoryAuthorizationFailure ||
    pendingRefresh?.purpose === "authorization";
  const mustNeutralizeOwnedView =
    authorizationReconciliation ||
    (hasDefinitiveBootstrap &&
      hasOwnedView &&
      !preservesKnownCreation &&
      (!bootstrap || selectedCompanyInvalid || viewOwnerUserId !== userId));

  useEffect(() => {
    if (!mustNeutralizeOwnedView || authorizationReconciliation) return;

    operationGeneration.current += 1;
    processedDirectoryFailure.current = null;
    setSelectedCompanyId("");
    setSelectedCompanyUserId("");
    setViewOwnerUserId("");
    setAttempt(null);
    attemptRef.current = null;
    setFeedback(null);
    setKnownCreatedOperation(null);
    setProfileEmployee(null);
    setPendingRefresh(null);
    refreshGate.current = false;
    form.reset();
  }, [authorizationReconciliation, form, mustNeutralizeOwnedView]);

  useEffect(() => {
    if (!pendingRefresh) return;

    if (pendingRefresh.phase === "pending") return;

    if (pendingRefresh.purpose === "authorization") {
      operationGeneration.current += 1;
      processedDirectoryFailure.current = null;
      setSelectedCompanyId("");
      setSelectedCompanyUserId("");
      setViewOwnerUserId("");
      setAttempt(null);
      attemptRef.current = null;
      setFeedback(null);
      setKnownCreatedOperation(null);
      setProfileEmployee(null);
      authorizationRefreshInFlight.current = false;
      refreshGate.current = false;
      form.reset();
      setPendingRefresh(null);
      return;
    }

    if (pendingRefresh.phase === "rejected") {
      if (
        pendingRefresh.purpose === "creation" &&
        pendingRefresh.operation.generation === operationGeneration.current
      ) {
        form.reset();
        setKnownCreatedOperation(pendingRefresh.operation);
      }
      refreshGate.current = false;
      setPendingRefresh(null);
      return;
    }

    if (
      isCurrentEligibleOperation(
        pendingRefresh.operation,
        operationGeneration,
        selectedCompany,
        userId,
      )
    ) {
      if (pendingRefresh.purpose === "creation") {
        form.reset();
        setFeedback("success");
      }
      setKnownCreatedOperation(null);
      void queryClient.invalidateQueries({
        queryKey: pendingRefresh.operation.queryKey,
      });
    } else if (pendingRefresh.purpose === "recovery") {
      setKnownCreatedOperation(null);
    }

    refreshGate.current = false;
    setPendingRefresh(null);
  }, [form, pendingRefresh, queryClient, selectedCompany, userId]);

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
          selectedCompanyIdRef,
          currentUserIdRef,
        )
      ) {
        return;
      }
      if (!result.ok) {
        if (isAmbiguous(result.error)) {
          setAttempt(operation);
          attemptRef.current = operation;
          return;
        }
        if (isAuthorizationFailure(result.error)) {
          setAttempt(null);
          attemptRef.current = null;
          await beginAuthorizationReconciliation(
            toConfirmedOperation(operation),
          );
          return;
        }
        setAttempt(null);
        attemptRef.current = null;
        setFeedback("error");
        return;
      }

      setAttempt(null);
      attemptRef.current = null;
      const confirmedOperation = toConfirmedOperation(operation);
      refreshGate.current = true;
      setPendingRefresh({
        operation: confirmedOperation,
        phase: "pending",
        purpose: "creation",
      });
      const refreshAccepted = await refreshBootstrap();
      setPendingRefresh({
        operation: confirmedOperation,
        phase: refreshAccepted ? "accepted" : "rejected",
        purpose: "creation",
      });
    },
  });

  const abandonAttempt = () => {
    operationGeneration.current += 1;
    setAttempt(null);
    attemptRef.current = null;
    setFeedback(null);
  };
  const updateForm = () => {
    setViewOwnerUserId(userId ?? "");
    abandonAttempt();
  };
  const selectCompany = (companyId: string) => {
    processedDirectoryFailure.current = null;
    if (selectedCompanyId && companyId !== selectedCompanyId) form.reset();
    setSelectedCompanyId(companyId);
    setSelectedCompanyUserId(userId ?? "");
    setViewOwnerUserId(userId ?? "");
    setProfileEmployee(null);
    abandonAttempt();
  };
  const reserve = (values: EmployeeForm) => {
    if (
      !selectedCompany ||
      !userId ||
      knownCreatedOperation ||
      attemptRef.current ||
      pendingRefresh ||
      mutation.isPending ||
      inFlight.current
    ) {
      return;
    }
    const command = {
      companyId: selectedCompany.companyId,
      displayName: values.displayName.trim(),
      email: values.email.trim(),
      requestId: createRequestId(),
    };
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate({
      command,
      companyId: selectedCompany.companyId,
      generation: operationGeneration.current,
      queryKey,
      userId,
    });
  };
  const retry = () => {
    if (
      !attemptRef.current ||
      inFlight.current ||
      !isCurrentOperation(
        attemptRef.current,
        operationGeneration,
        selectedCompanyIdRef,
        currentUserIdRef,
      )
    ) {
      return;
    }
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate(attemptRef.current);
  };
  const refreshKnownCreation = async () => {
    if (isRefreshingKnownCreation || !knownCreatedOperation) return;
    setIsRefreshingKnownCreation(true);
    const operation = knownCreatedOperation;
    refreshGate.current = true;
    setPendingRefresh({
      operation,
      phase: "pending",
      purpose: "recovery",
    });
    const refreshAccepted = await refreshBootstrap();
    setPendingRefresh({
      operation,
      phase: refreshAccepted ? "accepted" : "rejected",
      purpose: "recovery",
    });
    setIsRefreshingKnownCreation(false);
  };

  if (mustNeutralizeOwnedView) {
    return <CircularProgress aria-label="Atualizando pessoas" />;
  }

  if (knownCreatedOperation) {
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Atualize a sessão antes de consultar novamente os dados da empresa."
          eyebrow="Pessoas"
          title="Pessoas"
        />
        <Alert severity="info">
          A reserva foi concluída, mas a sessão precisa ser atualizada.
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
  }

  if (!bootstrap) return <CircularProgress aria-label="Carregando pessoas" />;

  if (eligibleCompanies.length === 0) {
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Consulte pessoas das empresas que estão disponíveis para sua sessão."
          eyebrow="Pessoas"
          title="Pessoas"
        />
        <Alert severity="info">
          Nenhuma empresa está disponível para esta ação.
        </Alert>
      </Stack>
    );
  }

  const directoryFailure = directory.data && !directory.data.ok;
  const profileCompanyName = selectedCompany?.companyName ?? "Empresa";

  return (
    <CompanyProvider company={selectedCompany ?? null}>
      <Stack spacing={3}>
        <PageHeader
          description="Selecione uma empresa para consultar funcionários e, se tiver permissão, reservar um novo localmente."
          eyebrow="Pessoas"
          title="Pessoas"
        />
        <Card component="section" variant="outlined">
          <CardContent>
            <Stack
              component="form"
              noValidate
              onSubmit={form.handleSubmit(reserve)}
              spacing={2}
            >
              <TextField
                label="Empresa"
                onChange={(event) => selectCompany(event.target.value)}
                select
                value={selectedCompany?.companyId ?? ""}
              >
                <MenuItem value="">Selecione uma empresa</MenuItem>
                {eligibleCompanies.map((company) => (
                  <MenuItem key={company.companyId} value={company.companyId}>
                    {company.companyName}
                  </MenuItem>
                ))}
              </TextField>
              <TextField
                {...form.register("displayName", { onChange: updateForm })}
                error={Boolean(form.formState.errors.displayName)}
                helperText={form.formState.errors.displayName?.message}
                label="Nome"
              />
              <TextField
                {...form.register("email", { onChange: updateForm })}
                error={Boolean(form.formState.errors.email)}
                helperText={form.formState.errors.email?.message}
                label="E-mail"
                type="email"
              />
              <Button
                disabled={
                  !selectedCompany ||
                  Boolean(attempt) ||
                  Boolean(pendingRefresh) ||
                  mutation.isPending
                }
                startIcon={<PersonAddAltOutlinedIcon />}
                type="submit"
                variant="contained"
              >
                Reservar funcionário
              </Button>
              <Typography color="text.secondary" variant="caption">
                Reservar cria um registro local de funcionário nesta empresa;
                não envia convite nem acesso.
              </Typography>
              {attempt && !mutation.isPending ? (
                <Button onClick={retry} variant="outlined">
                  Tentar novamente
                </Button>
              ) : null}
              {feedback === "success" ? (
                <Alert severity="success">Funcionário reservado.</Alert>
              ) : null}
              {feedback === "error" ? (
                <Alert severity="error">
                  Não foi possível concluir esta reserva.
                </Alert>
              ) : null}
            </Stack>
          </CardContent>
        </Card>
        {selectedCompany ? (
          <Card component="section" variant="outlined">
            <CardContent>
              <Stack spacing={1}>
                <Typography component="h2" variant="h6">
                  Funcionários
                </Typography>
                {directory.isPending ? (
                  <CircularProgress
                    aria-label="Carregando funcionários"
                    size={24}
                  />
                ) : null}
                {directoryFailure || directory.isError ? (
                  <Alert severity="error">
                    Não foi possível carregar as pessoas.
                  </Alert>
                ) : null}
                {directory.data?.ok && directory.data.items.length === 0 ? (
                  <Typography color="text.secondary">
                    Nenhum funcionário reservado.
                  </Typography>
                ) : null}
                {directory.data?.ok
                  ? directory.data.items.map((employee) => (
                      <Button
                        key={employee.employeeId}
                        onClick={() => setProfileEmployee(employee)}
                        startIcon={<PersonOutlinedIcon />}
                        sx={{ justifyContent: "flex-start", width: "100%" }}
                        variant="outlined"
                      >
                        {employee.displayName}
                      </Button>
                    ))
                  : null}
              </Stack>
            </CardContent>
          </Card>
        ) : null}
        <EmployeeProfileDialog
          companyName={profileCompanyName}
          employee={profileEmployee}
          onClose={() => setProfileEmployee(null)}
          open={Boolean(profileEmployee)}
        />
      </Stack>
    </CompanyProvider>
  );
}

function isAuthorizationFailure(failure: EmployeeDirectoryFailure): boolean {
  return failure.kind === "unauthorized" || failure.kind === "forbidden";
}

function isAmbiguous(failure: EmployeeDirectoryFailure): boolean {
  return (
    failure.kind === "network" ||
    failure.kind === "unavailable" ||
    failure.mayHaveCommitted === true
  );
}

function isCurrentOperation(
  operation: DirectoryOperation,
  operationGeneration: MutableRefObject<number>,
  selectedCompanyId: MutableRefObject<string>,
  userId: MutableRefObject<string | null>,
): boolean {
  return (
    operation.generation === operationGeneration.current &&
    operation.companyId === selectedCompanyId.current &&
    operation.userId === userId.current
  );
}

function isCurrentEligibleOperation(
  operation: ConfirmedOperation,
  operationGeneration: MutableRefObject<number>,
  selectedCompany:
    | { companyId: string; capabilities: readonly unknown[] }
    | undefined,
  userId: string | undefined,
): boolean {
  return (
    operation.generation === operationGeneration.current &&
    operation.companyId === selectedCompany?.companyId &&
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
