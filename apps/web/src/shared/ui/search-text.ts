// Busca amigável para nomes em pt-BR: remove acentos e compara sem caixa,
// para que quem busca "andre" encontre "André" e vice-versa. Compartilhada
// por todas as listas com campo de busca (Pessoas, Permissões).
export function normalizeSearchText(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("pt-BR")
    .trim();
}
