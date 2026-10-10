# Bouncy Castle C# 2.7.0

Официальный бинарный файл для .NET Framework 4.6.1, совместимый с целевой сборкой мода net472. AssemblyVersion — 2.0.0.0; FileVersion — 2.7.0.16391.

- DLL: https://downloads.bouncycastle.org/csharp/2.7.0/net461/BouncyCastle.Cryptography.dll
- Официальные хеши: https://downloads.bouncycastle.org/csharp/docs/CHECKSUMS.csv
- SHA-256: `4F96977E9C67334742C683410B3A361258219F0D3084A5E0BC10FBA96CF23A0D`
- Лицензия: https://github.com/bcgit/bc-csharp/blob/release-2.7.0/LICENSE.md (копия LICENSE.md рядом).
- Код DTLS: https://github.com/bcgit/bc-csharp/tree/release-2.7.0/crypto/src/tls

Хеш скачанного файла совпал со строкой `.NET 4.61` официального CSV. Сборка и транспорт при первом использовании отклоняют другую DLL, включая библиотеку из другого мода. Хеш обеспечивает воспроизводимость зависимости; это не проверка отсутствия уязвимостей и не подпись автора мода.
