import { z } from 'zod';

export const concurrencyLimitSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, 'Name is required'),
  limit: z
    .number({ error: 'Limit must be a positive integer' })
    .int('Limit must be a positive integer')
    .min(1, 'Limit must be a positive integer'),
});

export type ConcurrencyLimitFormValues = z.infer<typeof concurrencyLimitSchema>;
