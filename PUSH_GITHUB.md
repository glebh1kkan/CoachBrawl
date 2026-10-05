# Push CoachBrawl на GitHub

Репо: https://github.com/glebh1kkan/CoachBrawl (private)
Локально: `main`, `dev`, тег `v0.1-shuza-base`.

## Создать репо (ты)
https://github.com/new -> Owner: glebh1kkan, Name: CoachBrawl, Private, без README

## Пуш (я)
```bash
git remote set-url origin https://github.com/glebh1kkan/CoachBrawl.git
git push -u origin main dev --tags
```
Нужен токен с scope `repo` для private.
