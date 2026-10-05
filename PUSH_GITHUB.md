# Push LostBrawl на GitHub

Репо: https://github.com/glebh1kkan/LostBrawl (private)
Локально: `main`, `dev`, тег `v0.1-shuza-base`.

## 1. Создай пустой private репозиторий (1 минута, делаешь ты)
- Открой https://github.com/new
- Owner: glebh1kkan, Name: LostBrawl, Private
- НЕ ставь README / .gitignore / license -> Create repository

## 2. Запушь (делаю я, как только репо создан)
```bash
cd /root/LostBrawl
git push -u origin main dev --tags
```
Если спросит логин/токен — нужен твой Personal Access Token (Settings -> Developer settings -> classic, scope repo).

## Откаты
```bash
git checkout main && git merge dev && git tag v0.2-xxx && git push origin main dev --tags
git checkout main && git reset --hard v0.1-shuza-base  # откат
```
