"""按类描述符从 dexdump 输出里切出一个类的完整段。

    python tools/metasec/dex_slice.py dump.txt 'Lms/bd/c/f3;' > f3.txt
"""
import sys

def main(path, desc):
    out, capture, pending = [], False, ''
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith('Class #'):
                if capture:
                    break
                pending = line
                continue
            if not capture and line.strip().startswith('Class descriptor'):
                if desc in line:
                    capture = True
                    out.append(pending)
            if capture:
                out.append(line)
    sys.stdout.write(''.join(out))
    if not out:
        sys.stderr.write('未找到 ' + desc + '\n')

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
