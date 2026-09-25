BEGIN;

ALTER TABLE public.employee_personal_info
    ADD COLUMN IF NOT EXISTS id_card_same_as_current BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS id_card_province VARCHAR(150),
    ADD COLUMN IF NOT EXISTS id_card_district VARCHAR(150),
    ADD COLUMN IF NOT EXISTS id_card_subdistrict VARCHAR(150),
    ADD COLUMN IF NOT EXISTS id_card_postal_code VARCHAR(20),
    ADD COLUMN IF NOT EXISTS house_registration_same_as_current BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS house_registration_province VARCHAR(150),
    ADD COLUMN IF NOT EXISTS house_registration_district VARCHAR(150),
    ADD COLUMN IF NOT EXISTS house_registration_subdistrict VARCHAR(150),
    ADD COLUMN IF NOT EXISTS house_registration_postal_code VARCHAR(20);

-- Preserve the useful relationship for existing rows whose free-text addresses
-- were already identical, and initialise their structured address values.
UPDATE public.employee_personal_info
SET id_card_same_as_current = TRUE,
    id_card_province = residence_province,
    id_card_district = residence_district,
    id_card_subdistrict = residence_subdistrict,
    id_card_postal_code = residence_postal_code
WHERE NULLIF(BTRIM(id_card_address), '') IS NOT NULL
  AND BTRIM(id_card_address) = BTRIM(current_address);

UPDATE public.employee_personal_info
SET house_registration_same_as_current = TRUE,
    house_registration_province = residence_province,
    house_registration_district = residence_district,
    house_registration_subdistrict = residence_subdistrict,
    house_registration_postal_code = residence_postal_code
WHERE NULLIF(BTRIM(house_registration_address), '') IS NOT NULL
  AND BTRIM(house_registration_address) = BTRIM(current_address);

COMMIT;
