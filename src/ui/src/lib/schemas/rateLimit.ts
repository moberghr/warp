import { z } from 'zod';

export const rateLimitSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, 'Name is required'),
  count: z
    .number({ error: 'Count must be a positive integer' })
    .int('Count must be a positive integer')
    .min(1, 'Count must be a positive integer'),
  windowSeconds: z
    .number({ error: 'Window must be a positive integer (seconds)' })
    .int('Window must be a positive integer (seconds)')
    .min(1, 'Window must be a positive integer (seconds)'),
});

export type RateLimitFormValues = z.infer<typeof rateLimitSchema>;
