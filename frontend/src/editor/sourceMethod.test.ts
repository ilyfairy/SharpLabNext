import { describe, expect, it } from 'vitest'
import { findSourceMethodAtLine } from './sourceMethod'

describe('findSourceMethodAtLine', () => {
  it('finds the current C# generic method', () => {
    const source = `class C
{
    public static int Sum<T>(int value)
    {
        return value;
    }
}`
    expect(findSourceMethodAtLine(source, 'csharp', 6)?.name).toBe('Sum')
  })

  it('finds G# and Visual Basic functions', () => {
    expect(
      findSourceMethodAtLine('async func compute(n int32) int32 {\n return n\n}', 'gsharp', 2)
        ?.name,
    ).toBe('compute')
    expect(
      findSourceMethodAtLine(
        'Public Function Total() As Integer\n Return 1\nEnd Function',
        'visual-basic',
        2,
      )?.name,
    ).toBe('Total')
  })

  it('maps only a cursor inside C# top-level statements to <Main>$', () => {
    const source = 'using System;\nConsole.WriteLine(1);'
    expect(findSourceMethodAtLine(source, 'csharp', 1)).toBeNull()
    expect(findSourceMethodAtLine(source, 'csharp', 2)?.name).toBe('<Main>$')
  })

  it('does not mistake block namespaces or type members for top-level statements', () => {
    const source = `namespace Sample
{
    class Program
    {
        private int value;
    }
}`
    expect(findSourceMethodAtLine(source, 'csharp', 2)).toBeNull()
    expect(findSourceMethodAtLine(source, 'csharp', 5)).toBeNull()
  })

  it('does not fall back to <Main>$ when the cursor is inside a later type', () => {
    const source = `Console.WriteLine(1);

class Program
{
    private int value;
}`
    expect(findSourceMethodAtLine(source, 'csharp', 1)?.name).toBe('<Main>$')
    expect(findSourceMethodAtLine(source, 'csharp', 5)).toBeNull()
  })

  it('treats an attributed same-line type declaration as the end of top-level code', () => {
    const source = `[System.Obsolete] public static class Program
{
    private static int value;
}`
    expect(findSourceMethodAtLine(source, 'csharp', 1)).toBeNull()
    expect(findSourceMethodAtLine(source, 'csharp', 3)).toBeNull()
  })

  it('does not keep selecting a method after its body has ended', () => {
    const source = `class C
{
    int First()
    {
        return 1;
    }

    int value;
}`
    expect(findSourceMethodAtLine(source, 'csharp', 5)?.name).toBe('First')
    expect(findSourceMethodAtLine(source, 'csharp', 8)).toBeNull()
  })

  it('keeps expression-bodied methods scoped to their declaration line', () => {
    const source = `class C
{
    int First() => 1;
    int value;
}`
    expect(findSourceMethodAtLine(source, 'csharp', 3)?.name).toBe('First')
    expect(findSourceMethodAtLine(source, 'csharp', 4)).toBeNull()
  })

  it('finds PHP functions with method modifiers and by-reference returns', () => {
    const source = `<?php
final class Calculator
{
    public static function &square(
        int $value,
    ): int {
        $result = $value * $value;
        return $result;
    }
}`

    expect(findSourceMethodAtLine(source, 'php', 8, 'index.php')).toEqual({
      name: 'square',
      lineNumber: 4,
      jitMethodFilter: '*square*',
    })
    expect(findSourceMethodAtLine(source, 'php', 10, 'index.php')).toBeNull()
  })

  it('ignores PHP function-shaped text and braces in strings and comments', () => {
    const source = `<?php
// function commented() { }
$single = 'function single() { }';
$double = "function double() { }";
/*
function blocked() { }
*/
$template = <<<PHP_TEXT
function heredoc() { }
PHP_TEXT;
$literal = <<<'NOWDOC_TEXT'
function nowdoc() { }
NOWDOC_TEXT;

function real(): string
{
    $text = "} // not the body end";
    # function hashComment() { }
    return $text;
}
`

    expect(findSourceMethodAtLine(source, 'php', 19, 'index.php')).toMatchObject({
      name: 'real',
      lineNumber: 15,
    })
    expect(findSourceMethodAtLine(source, 'php', 12, 'index.php')).toBeNull()
  })

  it('does not treat anonymous or abstract PHP functions as executable current methods', () => {
    const source = `<?php
abstract class Base
{
    abstract protected function compute(int $value): int;
}

$callback = static function (int $value): int {
    return $value + 1;
};`

    expect(findSourceMethodAtLine(source, 'php', 4, 'index.php')).toBeNull()
    expect(findSourceMethodAtLine(source, 'php', 8, 'index.php')).toBeNull()
  })

  it('ignores function-shaped text outside PHP tags in mixed documents', () => {
    const source = `<script>
function browserOnly() { return 1; }
</script>
<?php
function serverOnly(): int {
    return 2;
}
?>
function plainHtml() {}`

    expect(findSourceMethodAtLine(source, 'php', 2, 'index.php')).toBeNull()
    expect(findSourceMethodAtLine(source, 'php', 6, 'index.php')).toMatchObject({
      name: 'serverOnly',
      jitMethodFilter: '*serverOnly*',
    })
    expect(findSourceMethodAtLine(source, 'php', 9, 'index.php')).toBeNull()
  })

  it('does not invent a JIT filter for PHP identifiers outside the verified CLR mapping set', () => {
    const selection = findSourceMethodAtLine(
      '<?php\nfunction 计算(int $value): int {\n    return $value;\n}',
      'php',
      3,
      'index.php',
    )

    expect(selection).toMatchObject({ name: '计算', jitMethodFilter: null })
  })
})
